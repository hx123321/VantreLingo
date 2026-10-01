using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace VantreLingo.Core;

// 免费免 key 翻译：谷歌网页接口 / 微软 Edge 免费 token，派生自固定基座的 BuiltIn 思路。
// 单次调用只跑所选的一个引擎，不自动切换；文本只发往所选引擎。
// 超长按 1000 字符分块（总量上限 5000），失败抛受控异常，永不写回半成品。
public static class FreeTranslators
{
    public const int ChunkSize = 1000;
    public const int MaxLength = 5000;

    private static readonly char[] BreakChars = ['\n', '。', '！', '？', '!', '?', ';', '；'];

    public static async Task<TranslationResult> TranslateAsync(HttpClient http, string engine,
        string text, LanguagePlan plan, int timeoutSeconds, CancellationToken cancellationToken)
    {
        if (engine is not ("google" or "microsoft")) throw new InvalidDataException("未知的免费引擎。");
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaxLength)
            throw new InvalidDataException("免费接口单次上限约 5000 字符，请分段或按 Ctrl 走模型。");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 5, 300)));
        try
        {
            return engine == "google"
                ? await Google.TranslateAsync(http, text, plan, timeout.Token)
                : await Microsoft.TranslateAsync(http, text, plan, timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("免费接口请求超时，原文保持不变。");
        }
    }

    internal static IEnumerable<string> Chunk(string text)
    {
        var i = 0;
        while (i < text.Length)
        {
            var len = Math.Min(ChunkSize, text.Length - i);
            if (len == ChunkSize && i + len < text.Length)
            {
                var absolute = text.LastIndexOfAny(BreakChars, i + len - 1, len);
                if (absolute - i > 200) len = absolute - i + 1;
            }
            yield return text.Substring(i, len);
            i += len;
        }
    }

    internal static string NormalizeCode(string code) =>
        code.Equals("zh-Hans", StringComparison.OrdinalIgnoreCase) ? "zh-CN" :
        code.Equals("zh-Hant", StringComparison.OrdinalIgnoreCase) ? "zh-TW" : code;

    private static class Google
    {
        internal static async Task<TranslationResult> TranslateAsync(HttpClient http, string text,
            LanguagePlan plan, CancellationToken ct)
        {
            if (plan.ExplicitTarget is { } explicitTarget)
            {
                var target = MapCode(explicitTarget);
                var source = plan.Source == "auto" ? "auto" : MapCode(plan.Source);
                var (explicitTranslation, explicitDetected) = await CallAsync(http, text, source, target, ct);
                return Build(text, explicitTranslation, explicitDetected ?? plan.Source, target, plan);
            }
            var autoSource = plan.Source == "auto" ? "auto" : MapCode(plan.Source);
            var (first, detectedRaw) = await CallAsync(http, text, autoSource, "zh-CN", ct);
            var detected = NormalizeCode(detectedRaw ?? "auto");
            string effective;
            try { effective = plan.TargetFor(detected); }
            catch (InvalidDataException) { effective = "zh-CN"; }
            if (effective.Equals("zh-CN", StringComparison.OrdinalIgnoreCase))
                return Build(text, first, detected, "zh-CN", plan);
            var (second, _) = await CallAsync(http, text, autoSource, MapCode(effective), ct);
            return Build(text, second, detected, effective, plan);
        }

        internal static string MapCode(string code)
        {
            if (code == "auto") return "auto";
            var fixedCode = NormalizeCode(code);
            LanguageRoutingService.ValidateCode(fixedCode);
            return fixedCode;
        }

        internal static async Task<(string Translation, string? Detected)> CallAsync(HttpClient http,
            string text, string source, string target, CancellationToken ct)
        {
            var output = new StringBuilder();
            string? detected = null;
            foreach (var chunk in Chunk(text))
            {
                var url = $"https://translate.google.com/translate_a/single?client=gtx&sl={source}&tl={target}&dt=t&q={Uri.EscapeDataString(chunk)}";
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                if (!response.IsSuccessStatusCode)
                    throw new HttpRequestException(
                        $"免费接口请求失败（HTTP {(int)response.StatusCode}），可能被限流，稍后重试或按 Ctrl 走模型。",
                        null, response.StatusCode);
                var body = await ReadBodyAsync(response, ct);
                try
                {
                    using var document = JsonDocument.Parse(body);
                    var root = document.RootElement;
                    foreach (var segment in root[0].EnumerateArray())
                        output.Append(segment[0].GetString());
                    if (detected is null && root.GetArrayLength() > 2 && root[2].ValueKind == JsonValueKind.String)
                        detected = root[2].GetString();
                }
                catch (Exception ex) when (ex is JsonException or InvalidOperationException or IndexOutOfRangeException or KeyNotFoundException)
                {
                    throw new InvalidDataException("免费接口返回异常，请稍后重试或按 Ctrl 走模型。");
                }
            }
            return (output.ToString(), detected);
        }
    }

    private static class Microsoft
    {
        private const string AuthUrl = "https://edge.microsoft.com/translate/auth";
        private const string Endpoint = "https://api-edge.cognitive.microsofttranslator.com/translate?api-version=3.0";
        private static readonly SemaphoreSlim TokenLock = new(1, 1);
        private static string? CachedToken;
        private static DateTimeOffset TokenExpiresAt;

        internal static async Task<TranslationResult> TranslateAsync(HttpClient http, string text,
            LanguagePlan plan, CancellationToken ct)
        {
            string? source = plan.Source == "auto" ? null : MapCode(plan.Source);
            if (plan.ExplicitTarget is { } explicitTarget)
            {
                var target = MapCode(explicitTarget) ?? throw new InvalidDataException("微软免费接口不支持该目标语言。");
                var (explicitTranslation, explicitDetected) = await CallAsync(http, text, source, target, ct);
                return Build(text, explicitTranslation, explicitDetected ?? plan.Source, target, plan);
            }
            var (first, detectedRaw) = await CallAsync(http, text, source, "zh-Hans", ct);
            var detected = NormalizeCode(detectedRaw ?? "auto");
            string effective;
            try { effective = plan.TargetFor(detected); }
            catch (InvalidDataException) { effective = "zh-CN"; }
            if (effective.Equals("zh-CN", StringComparison.OrdinalIgnoreCase))
                return Build(text, first, detected, "zh-CN", plan);
            var mapped = MapCode(effective) ?? throw new InvalidDataException("微软免费接口不支持该目标语言。");
            var (second, _) = await CallAsync(http, text, source, mapped, ct);
            return Build(text, second, detected, effective, plan);
        }

        internal static string? MapCode(string? code)
        {
            if (code is null || code == "auto") return null;
            if (code.StartsWith("yue", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("微软免费接口不支持该语言，请换引擎或按 Ctrl 走模型。");
            var mapped = NormalizeCode(code) switch
            {
                var c when c.Equals("zh-CN", StringComparison.OrdinalIgnoreCase) => "zh-Hans",
                var c when c.Equals("zh-TW", StringComparison.OrdinalIgnoreCase) => "zh-Hant",
                var c => c,
            };
            LanguageRoutingService.ValidateCode(NormalizeCode(mapped));
            return mapped;
        }

        internal static async Task<(string Translation, string? Detected)> CallAsync(HttpClient http,
            string text, string? source, string target, CancellationToken ct)
        {
            var token = await GetTokenAsync(http, ct);
            var output = new StringBuilder();
            string? detected = null;
            foreach (var chunk in Chunk(text))
            {
                var url = Endpoint + "&to=" + target + (source is null ? "" : "&from=" + source);
                using var request = new HttpRequestMessage(HttpMethod.Post, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                request.Content = JsonContent.Create(new[] { new { Text = chunk } });
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                if (!response.IsSuccessStatusCode)
                    throw new HttpRequestException(
                        $"免费接口请求失败（HTTP {(int)response.StatusCode}），可能被限流，稍后重试或按 Ctrl 走模型。",
                        null, response.StatusCode);
                var body = await ReadBodyAsync(response, ct);
                try
                {
                    using var document = JsonDocument.Parse(body);
                    var item = document.RootElement[0];
                    output.Append(item.GetProperty("translations")[0].GetProperty("text").GetString());
                    if (detected is null && item.TryGetProperty("detectedLanguage", out var detectedElement) &&
                        detectedElement.TryGetProperty("language", out var languageElement) &&
                        languageElement.ValueKind == JsonValueKind.String)
                        detected = languageElement.GetString();
                }
                catch (Exception ex) when (ex is JsonException or InvalidOperationException or IndexOutOfRangeException or KeyNotFoundException)
                {
                    throw new InvalidDataException("免费接口返回异常，请稍后重试或按 Ctrl 走模型。");
                }
            }
            return (output.ToString(), detected);
        }

        internal static async Task<string> GetTokenAsync(HttpClient http, CancellationToken ct)
        {
            if (CachedToken is not null && DateTimeOffset.UtcNow < TokenExpiresAt - TimeSpan.FromMinutes(1))
                return CachedToken;
            await TokenLock.WaitAsync(ct);
            try
            {
                if (CachedToken is not null && DateTimeOffset.UtcNow < TokenExpiresAt - TimeSpan.FromMinutes(1))
                    return CachedToken;
                using var response = await http.GetAsync(AuthUrl, ct);
                if (!response.IsSuccessStatusCode)
                    throw new HttpRequestException($"免费接口鉴权失败（HTTP {(int)response.StatusCode}）。", null, response.StatusCode);
                var token = (await response.Content.ReadAsStringAsync(ct)).Trim().Trim('"');
                if (string.IsNullOrWhiteSpace(token)) throw new InvalidDataException("免费接口鉴权失败，请稍后重试。");
                CachedToken = token;
                TokenExpiresAt = ParseExpiry(token) ?? DateTimeOffset.UtcNow.AddMinutes(5);
                return token;
            }
            finally { TokenLock.Release(); }
        }

        internal static DateTimeOffset? ParseExpiry(string token)
        {
            try
            {
                var parts = token.Split('.');
                if (parts.Length != 3) return null;
                var payload = parts[1].Replace('-', '+').Replace('_', '/');
                payload += new string('=', (4 - payload.Length % 4) % 4);
                using var document = JsonDocument.Parse(Convert.FromBase64String(payload));
                return document.RootElement.TryGetProperty("exp", out var exp) &&
                    exp.TryGetInt64(out var seconds)
                    ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null;
            }
            catch
            {
                return null;
            }
        }
    }

    private static TranslationResult Build(string source, string translation, string detected,
        string target, LanguagePlan plan)
    {
        if (string.IsNullOrWhiteSpace(translation) || translation.Length > 200_000)
            throw new InvalidDataException("免费接口返回空译文。");
        var from = NormalizeCode(detected);
        var to = NormalizeCode(target);
        LanguageRoutingService.ValidateCode(from);
        LanguageRoutingService.ValidateCode(to);
        var warnings = new List<string>();
        if (plan.Source != "auto" && !from.Equals(plan.Source, StringComparison.OrdinalIgnoreCase))
            warnings.Add("检测到的源语言与选择不一致，请确认。");
        return new(translation, from, to, true, [.. warnings]);
    }

    private static async Task<string> ReadBodyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var block = new byte[8192];
        int length;
        while ((length = await stream.ReadAsync(block, ct)) > 0)
        {
            if (buffer.Length + length > 1_048_576)
                throw new InvalidDataException("免费接口响应超过允许大小。");
            buffer.Write(block, 0, length);
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}
