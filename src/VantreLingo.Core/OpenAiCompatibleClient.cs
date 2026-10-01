using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using VantreLingo.Core.Configuration;

namespace VantreLingo.Core;

public sealed class OpenAiCompatibleClient(HttpClient httpClient, ProviderProfile provider, string? apiKey) : ITranslationClient
{
    public async Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken)
    {
        provider.Validate();
        if (string.IsNullOrWhiteSpace(request.Text) || request.Text.Length > 30_000)
            throw new InvalidDataException("请输入文本，单次上限为 30,000 字符。");
        var content = await CompleteJsonAsync(TranslationContract.ComposePrompt(request), request.Text, cancellationToken);
        return TranslationContract.Parse(content, request.Languages);
    }

    public async Task<string> CompleteJsonAsync(string systemPrompt, string text, CancellationToken cancellationToken)
    {
        provider.Validate();
        if (string.IsNullOrWhiteSpace(text) || text.Length > 30_000)
            throw new InvalidDataException("请输入文本，单次上限为 30,000 字符。");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(provider.TimeoutSeconds));
        using var message = new HttpRequestMessage(HttpMethod.Post, provider.ChatCompletionsUri());
        if (!string.IsNullOrWhiteSpace(apiKey))
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        foreach (var (key, value) in provider.ExtraHeaders)
            message.Headers.TryAddWithoutValidation(key, value);
        var payload = new Dictionary<string, object>
        {
            ["model"] = provider.Model, ["stream"] = false,
            ["messages"] = new[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = JsonSerializer.Serialize(new { source_text = text }) }
            },
        };
        if (provider.UseResponseFormat)
            payload["response_format"] = new { type = "json_object" };
        if (provider.Temperature is { } temperature) payload["temperature"] = temperature;
        if (provider.MaxOutputTokens is { } maxTokens) payload["max_tokens"] = maxTokens;
        message.Content = JsonContent.Create(payload);

        try
        {
            using var response = await httpClient.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"Provider 请求失败（HTTP {(int)response.StatusCode}）。请检查配置。",
                    null, response.StatusCode);
            // 不读取或显示 Provider 错误正文，响应限制避免异常响应耗尽内存。
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var buffer = new MemoryStream();
            var block = new byte[8192];
            int length;
            while ((length = await stream.ReadAsync(block, timeout.Token)) > 0)
            {
                if (buffer.Length + length > 1_048_576)
                    throw new InvalidDataException("Provider 响应超过允许大小。");
                buffer.Write(block, 0, length);
            }
            using var document = JsonDocument.Parse(buffer.ToArray());
            if (!document.RootElement.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
                throw new InvalidDataException("Provider 未返回约定的完整 JSON 结果。");
            var choice = choices[0];
            // 兼容 opencode / Go 自建网关：finish_reason 可能为 stop、end_turn 或缺省（null）。
            // length、content_filter、tool_calls 等仍视为截断，拒绝使用。
            string? finishReason = null;
            if (choice.TryGetProperty("finish_reason", out var finishElement) && finishElement.ValueKind != JsonValueKind.Null)
                finishReason = finishElement.GetString();
            if (finishReason is not null && !finishReason.Equals("stop", StringComparison.OrdinalIgnoreCase) &&
                !finishReason.Equals("end_turn", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Provider 结果未完整结束，无法使用截断或工具调用结果。");
            if (!choice.TryGetProperty("message", out var messageElement) ||
                !messageElement.TryGetProperty("content", out var contentElement))
                throw new InvalidDataException("Provider 未返回约定的完整 JSON 结果。");
            var content = ExtractMessageContent(contentElement);
            if (string.IsNullOrEmpty(content)) throw new InvalidDataException("Provider 返回空内容。");
            cancellationToken.ThrowIfCancellationRequested();
            return content;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Provider 请求超时，原文保持不变。");
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or IndexOutOfRangeException)
        {
            throw new InvalidDataException("Provider 未返回约定的完整 JSON 结果。");
        }
    }

    internal static string? ExtractMessageContent(JsonElement content)
    {
        // 标准为 string；部分 Go 网关返回 content 数组（文本块）。
        if (content.ValueKind == JsonValueKind.String) return content.GetString();
        if (content.ValueKind == JsonValueKind.Null || content.ValueKind == JsonValueKind.Undefined) return null;
        if (content.ValueKind != JsonValueKind.Array) return null;
        var parts = new List<string>();
        foreach (var item in content.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                if (item.GetString() is { } s && s.Length > 0) parts.Add(s);
            }
            else if (item.ValueKind == JsonValueKind.Object)
            {
                if (item.TryGetProperty("text", out var textElement))
                {
                    if (textElement.ValueKind == JsonValueKind.String)
                    {
                        if (textElement.GetString() is { } s && s.Length > 0) parts.Add(s);
                    }
                    else if (textElement.ValueKind == JsonValueKind.Object &&
                        textElement.TryGetProperty("value", out var nested) &&
                        nested.ValueKind == JsonValueKind.String &&
                        nested.GetString() is { } nestedText && nestedText.Length > 0)
                    {
                        parts.Add(nestedText);
                    }
                }
            }
        }
        return parts.Count == 0 ? null : string.Concat(parts);
    }
}
