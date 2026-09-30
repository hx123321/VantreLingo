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
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(provider.TimeoutSeconds));
        using var message = new HttpRequestMessage(HttpMethod.Post, provider.ChatCompletionsUri());
        if (!string.IsNullOrWhiteSpace(apiKey))
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        message.Content = JsonContent.Create(new
        {
            model = provider.Model,
            stream = false,
            messages = new[]
            {
                new { role = "system", content = TranslationContract.SystemPrompt(request.Languages) },
                new { role = "user", content = JsonSerializer.Serialize(new { source_text = request.Text }) }
            },
            response_format = new { type = "json_object" }
        });

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
            var choice = document.RootElement.GetProperty("choices")[0];
            if (choice.GetProperty("finish_reason").GetString() != "stop")
                throw new InvalidDataException("Provider 结果未完整结束，无法使用截断或工具调用结果。");
            var content = choice
                .GetProperty("message").GetProperty("content").GetString();
            if (content is null) throw new InvalidDataException("Provider 返回空内容。");
            return TranslationContract.Parse(content, request.Languages);
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
}
