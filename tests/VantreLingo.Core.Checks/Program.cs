using System.Net;
using System.Text;
using System.Text.Json;
using VantreLingo.Core;
using VantreLingo.Core.Configuration;
using VantreLingo.Core.Operations;

var checks = new List<(string Name, Func<Task> Check)>();
var temporaryRoot = Path.Combine(Directory.GetCurrentDirectory(), ".tmp", "checks", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temporaryRoot);
var plan = LanguageRoutingService.Plan("auto", null, new());

void Add(string name, Action check) => checks.Add((name, () => { check(); return Task.CompletedTask; }));
void AddAsync(string name, Func<Task> check) => checks.Add((name, check));
void Assert(bool condition) { if (!condition) throw new Exception("断言失败。"); }
void Throws<T>(Action action) where T : Exception
{
    try { action(); }
    catch (T) { return; }
    throw new Exception($"预期异常 {typeof(T).Name}。");
}
async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
{
    try { await action(); }
    catch (T) { return; }
    throw new Exception($"预期异常 {typeof(T).Name}。");
}

Add("迟到请求不能发布结果或副作用", () =>
{
    using var coordinator = new OperationCoordinator();
    using var first = coordinator.Begin();
    using var second = coordinator.Begin();
    var displayed = "";
    Assert(first.Token.IsCancellationRequested);
    Assert(coordinator.TryPublish(second, () => displayed = "B"));
    Assert(!coordinator.TryPublish(first, () => displayed = "A"));
    Assert(displayed == "B");
});
Add("取消后的请求失去副作用所有权", () =>
{
    using var coordinator = new OperationCoordinator();
    using var operation = coordinator.Begin();
    coordinator.Cancel();
    Assert(operation.Token.IsCancellationRequested);
    Assert(!coordinator.TryPublish(operation, () => throw new Exception("取消后发生了写入。")));
});
Add("已释放的操作不会在下一次取消时抛异常", () =>
{
    using var coordinator = new OperationCoordinator();
    var first = coordinator.Begin();
    first.Dispose();
    using var second = coordinator.Begin();
    Assert(coordinator.TryPublish(second, () => { }));
});
AddAsync("并发迟到结果不能覆盖最新请求", async () =>
{
    using var coordinator = new OperationCoordinator();
    var operations = Enumerable.Range(0, 100).Select(_ => coordinator.Begin()).ToArray();
    long displayed = 0;
    try
    {
        await Task.WhenAll(operations.Reverse().Select(op => Task.Run(() =>
            coordinator.TryPublish(op, () => displayed = op.Id))));
        Assert(displayed == operations[^1].Id);
    }
    finally { foreach (var operation in operations) operation.Dispose(); }
});
Add("首次中文目标是英文", () => Assert(plan.TargetFor("zh-CN") == "en"));
Add("日文阅读后中文沿用日文目标", () =>
{
    var japanese = new TranslationResult("你好", "ja", "zh-CN", true, []);
    var memory = LanguageRoutingService.MemoryCandidate(plan, japanese);
    Assert(memory == "ja");
    Assert(LanguageRoutingService.Plan("auto", null, new() { LastForeignLanguage = memory }).TargetFor("zh-CN") == "ja");
});
Add("手动中文到法文成功后允许记录外语", () =>
{
    var manual = LanguageRoutingService.Plan("zh-CN", "fr", new());
    Assert(LanguageRoutingService.MemoryCandidate(manual, new("bonjour", "zh-CN", "fr", true, [])) == "fr");
});
Add("不可靠识别不能更新记忆", () =>
    Assert(LanguageRoutingService.MemoryCandidate(plan, new("", "ja", "zh-CN", false, [])) is null));
Add("纯数字、型号、URL、模板和代码不构成语言证据", () =>
{
    foreach (var text in new[] { "5000", "SKU-123", "https://example.test", "{{name}}", "var price = 30;", "5,000 pcs" })
        Assert(!TranslationContract.HasLanguageEvidence(text));
    Assert(TranslationContract.HasLanguageEvidence("你好"));
    Assert(TranslationContract.HasLanguageEvidence("Please quote 5,000 pcs."));
});
Add("固定目标优先于智能目标", () =>
    Assert(LanguageRoutingService.Plan("auto", null, new() { Mode = "fixed", FixedTarget = "de" }).TargetFor("ja") == "de"));
Add("中文和无效语言不能作为最近外语", () =>
{
    Throws<InvalidDataException>(() => new AppSettings { Language = new() { LastForeignLanguage = "zh-CN" } }.Validate());
    Throws<InvalidDataException>(() => new AppSettings { Language = new() { LastForeignLanguage = "invalid-language" } }.Validate());
});
Add("正式文件数字变化会产生审查风险", () =>
{
    const string original = "5,000 pcs; USD 0.85; 30% deposit; 230 × 70 × 40 mm; 2026-12-10";
    Assert(TranslationContract.FindProtectionRisks(original, original).Count == 0);
    Assert(TranslationContract.FindProtectionRisks(original, original.Replace("0.85", "0.95")).Count > 0);
    Assert(TranslationContract.FindProtectionRisks(original, original.Replace("5,000", "500")).Count > 0);
});
Add("重复数字、邮箱和模板变量丢失会产生风险", () =>
{
    Assert(TranslationContract.FindProtectionRisks("10 and 10", "10").Count > 0);
    Assert(TranslationContract.FindProtectionRisks("a@example.test {{name}}", "a@example.test").Count > 0);
});
Add("模型返回与本地路由不符时拒绝", () =>
    Throws<InvalidDataException>(() => TranslationContract.Parse(
        "{\"translation\":\"hello\",\"source_language\":\"ja\",\"target_language\":\"en\",\"source_confident\":true,\"warnings\":[]}", plan)));
Add("拒绝缺字段和空译文的模型结构", () =>
{
    Throws<InvalidDataException>(() => TranslationContract.Parse("{\"translation\":\"hello\"}", plan));
    Throws<InvalidDataException>(() => TranslationContract.Parse(
        "{\"translation\":\"\",\"source_language\":\"zh-CN\",\"target_language\":\"en\",\"source_confident\":true,\"warnings\":[]}", plan));
});
Add("配置使用版本化 JSON 并原子覆盖", () =>
{
    var path = Path.Combine(temporaryRoot, "settings.json");
    var store = new JsonFileStore<AppSettings>(path, () => new(), s => s.Validate());
    Assert(store.Load().Writing.Mode == "review");
    Assert(!File.Exists(path));
    store.Save(new() { Language = new() { LastForeignLanguage = "ja" } });
    Assert(store.Load().Language.LastForeignLanguage == "ja");
    store.Save(new() { Language = new() { LastForeignLanguage = "de" } });
    Assert(store.Load().Language.LastForeignLanguage == "de");
    Assert(!Directory.EnumerateFiles(Path.Combine(temporaryRoot, ".tmp")).Any());
});
Add("损坏或未知版本的配置保持原样", () =>
{
    var path = Path.Combine(temporaryRoot, "corrupt.json");
    var store = new JsonFileStore<AppSettings>(path, () => new(), s => s.Validate());
    File.WriteAllText(path, "not json");
    Throws<InvalidDataException>(() => store.Load());
    Assert(File.ReadAllText(path) == "not json");
    File.WriteAllText(path, "{\"schema_version\":999}");
    Throws<InvalidDataException>(() => store.Load());
    Assert(File.ReadAllText(path) == "{\"schema_version\":999}");
});
Add("不落盘正文、历史或遥测设置", () =>
{
    var json = JsonSerializer.Serialize(new AppSettings(), JsonFormat.Options);
    Assert(!json.Contains("original_text") && !json.Contains("translation\""));
    Throws<InvalidDataException>(() => new AppSettings { Privacy = new() { Telemetry = true } }.Validate());
    new AppSettings { Writing = new() { Mode = "fast" } }.Validate();
    Throws<InvalidDataException>(() => new AppSettings { Writing = new() { Mode = "automatic" } }.Validate());
});
Add("拒绝非加密远程地址和 URL 内凭据", () =>
{
    Throws<InvalidDataException>(() => new ProviderProfile { Endpoint = "http://example.test/v1" }.ChatCompletionsUri());
    Throws<InvalidDataException>(() => new ProviderProfile { Endpoint = "https://key@example.test/v1" }.ChatCompletionsUri());
    Throws<InvalidDataException>(() => new ProviderProfile { Endpoint = "https://example.test/v1?api_key=example" }.ChatCompletionsUri());
    Assert(new ProviderProfile { Endpoint = "http://localhost:8000/v1/" }.ChatCompletionsUri().AbsolutePath == "/v1/chat/completions");
    Assert(new ProviderProfile { Endpoint = "https://example.test/v1/chat/completions" }.ChatCompletionsUri().AbsolutePath == "/v1/chat/completions");
});
Add("多个 Provider ID 唯一且必须明确选择", () =>
{
    new ProviderSettings { Providers = [new(), new() { Id = "secondary" }] }.Validate();
    Throws<InvalidDataException>(() => new ProviderSettings { Providers = [new(), new()] }.Validate());
    Throws<InvalidDataException>(() => new ProviderSettings { SelectedProviderId = "missing" }.Validate());
});
AddAsync("HTTP 适配器发送单次非流式结构化请求", async () =>
{
    var calls = 0;
    using var http = new HttpClient(new StubHandler(async (message, token) =>
    {
        calls++;
        Assert(message.Method == HttpMethod.Post && message.RequestUri!.AbsolutePath == "/v1/chat/completions");
        Assert(message.Headers.Authorization?.Parameter == "test-key-only");
        using var payload = JsonDocument.Parse(await message.Content!.ReadAsStringAsync(token));
        Assert(!payload.RootElement.GetProperty("stream").GetBoolean());
        Assert(payload.RootElement.GetProperty("response_format").GetProperty("type").GetString() == "json_object");
        using var user = JsonDocument.Parse(payload.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!);
        Assert(user.RootElement.GetProperty("source_text").GetString() == "你好");
        var inner = "{\"translation\":\"Hello\",\"source_language\":\"zh-CN\",\"target_language\":\"en\",\"source_confident\":true,\"warnings\":[]}";
        return JsonResponse(new { choices = new[] { new { finish_reason = "stop", message = new { content = inner } } } });
    }));
    var client = new OpenAiCompatibleClient(http, new() { Model = "test-model" }, "test-key-only");
    Assert((await client.TranslateAsync(new("你好", plan), CancellationToken.None)).Translation == "Hello");
    Assert(calls == 1);
});
AddAsync("Provider 错误不泄露正文且不自动重试", async () =>
{
    var calls = 0;
    using var http = new HttpClient(new StubHandler((_, _) =>
    {
        calls++;
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
        { Content = new StringContent("sensitive source text and secret") });
    }));
    var client = new OpenAiCompatibleClient(http, new() { Model = "test-model" }, null);
    try
    {
        await client.TranslateAsync(new("你好", plan), CancellationToken.None);
        throw new Exception("预期 HTTP 请求失败。");
    }
    catch (HttpRequestException ex)
    {
        Assert(ex.StatusCode == HttpStatusCode.Unauthorized && !ex.Message.Contains("sensitive"));
    }
    Assert(calls == 1);
});
AddAsync("HTTP 请求响应超限时拒绝", async () =>
{
    using var http = new HttpClient(new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
    { Content = new StringContent(new string('a', 1_048_577)) })));
    await ThrowsAsync<InvalidDataException>(() => new OpenAiCompatibleClient(http, new() { Model = "test-model" }, null)
        .TranslateAsync(new("你好", plan), CancellationToken.None));
});
AddAsync("截断的 Provider 结果不能当作完整译文", async () =>
{
    using var http = new HttpClient(new StubHandler((_, _) => Task.FromResult(JsonResponse(new
    {
        choices = new[] { new { finish_reason = "length", message = new { content = "{}" } } }
    }))));
    await ThrowsAsync<InvalidDataException>(() => new OpenAiCompatibleClient(http, new() { Model = "test-model" }, null)
        .TranslateAsync(new("你好", plan), CancellationToken.None));
});
AddAsync("用户取消传播到 HTTP，不能返回迟到结果", async () =>
{
    var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    using var cancellation = new CancellationTokenSource();
    using var http = new HttpClient(new StubHandler(async (_, token) =>
    {
        started.SetResult();
        await Task.Delay(Timeout.InfiniteTimeSpan, token);
        throw new Exception("取消失败。");
    }));
    var task = new OpenAiCompatibleClient(http, new() { Model = "test-model" }, null)
        .TranslateAsync(new("你好", plan), cancellation.Token);
    await started.Task;
    cancellation.Cancel();
    await ThrowsAsync<OperationCanceledException>(() => task);
});
AddAsync("请求超时与用户取消区分", async () =>
{
    using var http = new HttpClient(new StubHandler(async (_, token) =>
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, token);
        throw new Exception("超时未生效。");
    }));
    await ThrowsAsync<TimeoutException>(() => new OpenAiCompatibleClient(http, new() { Model = "test-model", TimeoutSeconds = 5 }, null)
        .TranslateAsync(new("你好", plan), CancellationToken.None));
});

// 可验证选区包含偏移和整个控件的上下文指纹，重复文本不能替代原范围。
const string controlText = "prefix 你好 suffix 你好";
var snapshot = new SelectionSnapshot(1, 42, 100, null, "Win32.Edit", "你好",
    SelectionSnapshot.Hash("你好"), DateTimeOffset.UtcNow,
    new SelectionLocator(200, 7, 9, SelectionSnapshot.Hash(controlText)));
var selection = new SelectionState(42, 100, 200, 7, 9, controlText, true, true);

Add("首次默认审查，快速模式可保存并恢复", () =>
{
    Assert(new AppSettings().Writing.Mode == "review");
    var store = new JsonFileStore<AppSettings>(Path.Combine(temporaryRoot, "writing.json"), () => new(), s => s.Validate());
    store.Save(new() { Writing = new() { Mode = "fast" } });
    Assert(store.Load().Writing.Mode == "fast");
});
Add("阅读意图无论风险和目标状态都不能写回", () =>
    Assert(WritebackGate.Validate(snapshot, selection, WritebackIntent.Read, "Hello", []) is not null));
Add("完整有效选区可进入快速写回", () =>
    Assert(WritebackGate.Validate(snapshot, selection, WritebackIntent.Fast, "Hello", []) is null));
Add("切换窗口、进程、控件或焦点阻止写回", () =>
{
    foreach (var changed in new[]
    {
        selection with { ProcessId = 43 }, selection with { TopLevelWindow = 101 },
        selection with { Control = 201 }, selection with { HasExpectedFocus = false },
        selection with { IsWritable = false }
    })
    {
        Assert(WritebackGate.Validate(snapshot, changed, WritebackIntent.Fast, "Hello", []) is not null);
        Assert(WritebackGate.Validate(snapshot, changed, WritebackIntent.Review, "Hello", []) is not null);
    }
});
Add("原文或上下文修改后审查和快速都不能写回", () =>
{
    foreach (var changed in new[] { selection with { Text = "prefix 你好 changed" }, selection with { Text = controlText.Replace("你好", "您好") } })
    {
        Assert(WritebackGate.Validate(snapshot, changed, WritebackIntent.Fast, "Hello", []) is not null);
        Assert(WritebackGate.Validate(snapshot, changed, WritebackIntent.Review, "Hello", []) is not null);
    }
});
Add("相同原文的另一处选区不能冒充原范围", () =>
    Assert(WritebackGate.Validate(snapshot, selection with { Start = 17, End = 19 }, WritebackIntent.Fast, "Hello", []) is not null));
Add("无法验证范围时只能审查复制", () =>
{
    Assert(WritebackGate.Validate(snapshot with { Locator = null }, selection, WritebackIntent.Fast, "Hello", []) is not null);
    Assert(WritebackGate.Validate(snapshot with { Locator = null }, selection, WritebackIntent.Review, "Hello", []) is not null);
});
Add("损坏或越界的范围不会写入", () =>
{
    foreach (var changed in new[] { selection with { Start = -1 }, selection with { End = 200 }, selection with { End = 7 } })
        Assert(WritebackGate.Validate(snapshot, changed, WritebackIntent.Fast, "Hello", []) is not null);
    Assert(WritebackGate.Validate(snapshot with { OriginalHash = "invalid" }, selection, WritebackIntent.Fast, "Hello", []) is not null);
});
Add("风险阻止自动写回，人工审查仍可明确应用", () =>
{
    Assert(WritebackGate.Validate(snapshot, selection, WritebackIntent.Fast, "Hello", ["risk"]) is not null);
    Assert(WritebackGate.Validate(snapshot, selection, WritebackIntent.Review, "Hello", ["risk"]) is null);
});
Add("空、过长及带空字符的结果不能写回", () =>
{
    foreach (var text in new[] { "", "  ", "hello\0world", new string('a', 30_001) })
        Assert(WritebackGate.Validate(snapshot, selection, WritebackIntent.Fast, text, []) is not null);
});
Add("追加一次提交原选区和译文，替换只提交译文", () =>
{
    Assert(WritebackGate.Replacement(snapshot, "Hello", WritebackAction.Append) == "你好Hello");
    Assert(WritebackGate.Replacement(snapshot, "Hello", WritebackAction.Replace) == "Hello");
});
Add("取消和迟到操作无法进入写回或更新记忆", () =>
{
    using var coordinator = new OperationCoordinator();
    using var first = coordinator.Begin();
    using var latest = coordinator.Begin();
    var applied = 0;
    var memory = "ja";
    void Apply() { applied++; memory = "fr"; }
    Assert(!coordinator.TryPublish(first, Apply));
    coordinator.Cancel();
    Assert(!coordinator.TryPublish(latest, Apply));
    Assert(applied == 0 && memory == "ja");
});
Add("保护中文相邻数字、单位、型号和代码", () =>
{
    foreach (var pair in new[]
    {
        (Source: "数量5000件", Target: "Quantity 500"),
        (Source: "230 mm", Target: "230 cm"),
        (Source: "USD 30", Target: "EUR 30"),
        (Source: "型号SKU-ABC", Target: "Model SKU-XYZ"),
        (Source: "Use `price = 30`", Target: "使用 `price = 40`"),
        (Source: "${customer} {{quantity}}", Target: "${client} {{quantity}}")
    }) Assert(TranslationContract.FindProtectionRisks(pair.Source, pair.Target).Count > 0);
    Assert(TranslationContract.FindProtectionRisks("Use 230 mm SKU-ABC", "使用 230 mm SKU-ABC").Count == 0);
});
Add("新增明确承诺和遗漏否定或条件产生风险", () =>
{
    Assert(TranslationContract.FindProtectionRisks("Please quote.", "We guarantee delivery.").Count > 0);
    Assert(TranslationContract.FindProtectionRisks("No logo.", "Add a logo.").Count > 0);
    Assert(TranslationContract.FindProtectionRisks("Only when approved.", "Start production.").Count > 0);
    Assert(TranslationContract.FindProtectionRisks("不要 Logo", "No logo").Count == 0);
});
Add("Diff 可还原原文和译文，支持空字符串和 emoji", () =>
{
    foreach (var pair in new[] { ("你好", "Hello"), ("same", "same"), ("", "hello"), ("hello", ""), ("A😀B", "A😁B"), ("😀", "😁😀") })
    {
        var diff = TextDifference.Between(pair.Item1, pair.Item2);
        Assert(diff.Prefix + diff.Removed + diff.Suffix == pair.Item1);
        Assert(diff.Prefix + diff.Added + diff.Suffix == pair.Item2);
        Assert(diff.Prefix.Length == 0 || !char.IsHighSurrogate(diff.Prefix[^1]));
        Assert(diff.Suffix.Length == 0 || !char.IsLowSurrogate(diff.Suffix[0]));
    }
});

var failures = 0;
try
{
    foreach (var (name, check) in checks)
    {
        try { await check(); Console.WriteLine($"通过：{name}"); }
        catch (Exception ex) { failures++; Console.Error.WriteLine($"失败：{name}（{ex.GetType().Name}）"); }
    }
    Console.WriteLine($"{checks.Count - failures}/{checks.Count} 项检查通过。");
}
finally { Directory.Delete(temporaryRoot, recursive: true); }
return failures == 0 ? 0 : 1;

static HttpResponseMessage JsonResponse(object value) => new(HttpStatusCode.OK)
{
    Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json")
};

sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handle) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        handle(request, cancellationToken);
}
