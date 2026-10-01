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
Add("中文目标判断覆盖简繁与大小写", () =>
{
    foreach (var code in new[] { "zh-CN", "zh-TW", "zh", "ZH-cn" }) Assert(LanguageRoutingService.IsChinese(code));
    foreach (var code in new[] { "en", "ja", "de", "fr" }) Assert(!LanguageRoutingService.IsChinese(code));
});
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
    // 快捷键名 paste_translation 含有 translation 子串，需带前引号精确匹配正文字段。
    Assert(!json.Contains("original_text") && !json.Contains("\"translation\":"));
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
    Assert(!new AppSettings().Capture.AutoCopyFallback);
    var store = new JsonFileStore<AppSettings>(Path.Combine(temporaryRoot, "writing.json"), () => new(), s => s.Validate());
    store.Save(new() { Writing = new() { Mode = "fast" }, Capture = new() { AutoCopyFallback = true } });
    var loaded = store.Load();
    Assert(loaded.Writing.Mode == "fast" && loaded.Capture.AutoCopyFallback);
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

Add("风格冲突在编辑、导入和执行前统一拒绝", () =>
{
    foreach (var style in new[]
    {
        new StylePreset { Scene = "formal_document", Tone = "humorous", Fidelity = "strict" },
        new StylePreset { Scene = "formal_document", Persona = "lively", Fidelity = "strict" },
        new StylePreset { Fidelity = "strict", Length = "concise" },
        new StylePreset { Tone = "humorous", Persona = "literal_rigid" }
    })
    {
        Throws<InvalidDataException>(style.Validate);
        Throws<InvalidDataException>(() => new PresetCatalog { Presets = [style] }.Validate());
        Throws<InvalidDataException>(() => TranslationContract.ComposePrompt(new("你好", plan, style)));
    }
    Assert(new StylePreset { Scene = "formal_document" }.Normalized().Fidelity == "strict");
});
Add("自定义提示测试并授权，修改后失效，导入不可继承授权", () =>
{
    var style = new StylePreset { CustomPrompt = "使用简洁商务表达" };
    Assert(!style.CanUseFast);
    Throws<InvalidDataException>(() => style.ApproveFast());
    var approved = style.Tested().ApproveFast();
    Assert(approved.CanUseFast);
    Assert(!(approved with { CustomPrompt = "换一种表达" }).CanUseFast);
    Assert(!(approved with { Tone = "humorous" }).CanUseFast);
    Assert(!new PresetCatalog { Presets = [approved] }.Imported().Presets[0].CanUseFast);
});
Add("公开示例可导入，术语种子保持草稿", () =>
{
    var presets = JsonSerializer.Deserialize<PresetCatalog>(File.ReadAllText("examples/presets.example.json"), JsonFormat.Options)!;
    var glossary = JsonSerializer.Deserialize<GlossaryCatalog>(File.ReadAllText("examples/glossary.example.json"), JsonFormat.Options)!;
    presets.Validate(); glossary.Validate();
    Assert(glossary.Terms.All(t => t.Status == "draft"));
    Assert(glossary.Match("气垫梳", "zh-CN", "en", ["product"]).Count == 0);
});
Add("术语最长短语优先并遵守语言和上下文", () =>
{
    var glossary = new GlossaryCatalog { Terms = [
        new() { Id = "short", Status = "active", Source = "梳", Target = "comb", Contexts = ["product"] },
        new() { Id = "long", Status = "active", Source = "气垫梳", Target = "cushion brush", Contexts = ["product"] }] };
    var matches = glossary.Match("这款气垫梳", "zh-CN", "en", ["product"]);
    Assert(matches.Count == 1 && matches[0].Source == "气垫梳");
    Assert(glossary.Match("气垫梳", "zh-CN", "ja", ["product"]).Count == 0);
    Assert(glossary.Match("气垫梳", "zh-CN", "en", ["software"]).Count == 0);
    Assert(glossary.Candidates("气垫梳", LanguageRoutingService.Plan("auto", "ja", new()), ["product"]).Count == 0);
});
Add("本地直译命中 active 词条时不调用模型", () =>
{
    var glossary = new GlossaryCatalog { Terms = [
        new() { Id = "t1", Status = "active", SourceLanguage = "zh-CN", TargetLanguage = "en", Source = "气垫梳", Target = "cushion brush", Direction = "forward" }] };
    var plan = LanguageRoutingService.Plan("auto", null, new());
    var hit = DirectTranslation.TryTranslate("气垫梳", plan, glossary);
    Assert(hit is not null && hit.Translation == "cushion brush" && hit.TargetLanguage == "en");
    Assert(DirectTranslation.TryTranslate("不存在的词", plan, glossary) is null);
    var draft = new GlossaryCatalog { Terms = [
        new() { Id = "t1", Status = "draft", SourceLanguage = "zh-CN", TargetLanguage = "en", Source = "气垫梳", Target = "cushion brush", Direction = "forward" }] };
    Assert(DirectTranslation.TryTranslate("气垫梳", plan, draft) is null);
    var fixedDe = LanguageRoutingService.Plan("auto", "de", new());
    Assert(DirectTranslation.TryTranslate("气垫梳", fixedDe, glossary) is null);
    var both = new GlossaryCatalog { Terms = [
        new() { Id = "t2", Status = "active", SourceLanguage = "en", TargetLanguage = "zh-CN", Source = "brush", Target = "刷", Direction = "both" }] };
    Assert(DirectTranslation.TryTranslate("刷", LanguageRoutingService.Plan("auto", null, new()), both)?.Translation == "brush");
});
Add("术语方向与拉丁词边界，草稿和禁用词不应用", () =>
{
    var glossary = new GlossaryCatalog { Terms = [new() { Id = "one", Status = "active", SourceLanguage = "en", TargetLanguage = "zh-CN", Source = "brush", Target = "刷", Direction = "both" }] };
    Assert(glossary.Match("brush toothbrush brushes", "en", "zh-CN", []).Count == 1);
    Assert(glossary.Match("刷", "zh-CN", "en", []).Single().Target == "brush");
    Assert((glossary with { Terms = [glossary.Terms[0] with { Status = "disabled" }] }).Match("brush", "en", "zh-CN", []).Count == 0);
});
Add("冲突 active 术语拒绝保存，重叠上下文和语言变体也拒绝", () =>
{
    var a = new GlossaryTerm { Id = "a", Status = "active", SourceLanguage = "en", TargetLanguage = "zh-CN", Source = "brush", Target = "刷", Contexts = ["product"] };
    var b = a with { Id = "b", SourceLanguage = "en-US", Target = "梳", Contexts = ["product", "oem_manufacturing"] };
    Throws<InvalidDataException>(() => new GlossaryCatalog { Terms = [a, b] }.Validate());
    new GlossaryCatalog { Terms = [a, b with { Status = "draft" }] }.Validate();
    new GlossaryCatalog { Terms = [a, b with { Contexts = ["software"] }] }.Validate();
});
Add("允许变体与禁用旧词的译后校验", () =>
{
    var g = new GlossaryCatalog { Terms = [new() { Status = "active", Source = "气垫梳", Target = "cushion brush", AllowedVariants = ["cushioned brush"], ForbiddenVariants = ["air comb"] }] };
    var matched = g.Match("气垫梳", "zh-CN", "en", []);
    Assert(GlossaryCatalog.FindRisks("cushioned brush", matched).Count == 0);
    Assert(GlossaryCatalog.FindRisks("air comb", matched).Count > 0);
    Assert(GlossaryCatalog.FindRisks("cushion brush and air comb", matched).Count > 0);
});
Add("完整配置 Prompt 中保留风格和语言限定术语", () =>
{
    var prompt = TranslationContract.ComposePrompt(new("气垫梳", plan, new() { Scene = "product" },
        new() { Terms = [new() { Status = "active", Source = "气垫梳", Target = "cushion brush" }] }));
    Assert(prompt.Contains("cushion brush") && prompt.Contains("source_language") && prompt.Contains("product"));
});
Add("正式文件责任主体、义务强度和格式变化产生风险", () =>
{
    Assert(FormalContentValidator.FindRisks("We shall deliver.", "我方应当交付。").Count == 0);
    Assert(FormalContentValidator.FindRisks("We shall deliver.", "贵方可以交付。").Count > 0);
    Assert(FormalContentValidator.FindRisks("Line one\nLine two", "Combined line").Count > 0);
    Assert(TranslationContract.FindProtectionRisks("尺寸230mm", "Size 230cm").Count > 0);
});
Add("模型无证据公司名和不支持的字段值不会显示为 known", () =>
{
    var report = InquiryService.Validate("Please quote brushes.", new() { Customer = new() { ["company"] = new() { Status = "known", Value = "Invented Ltd", Evidence = "Invented Ltd" } } });
    Assert(report.Customer["company"].Status == "ambiguous" && report.Customer["company"].Value is null);
    report = InquiryService.Validate("Hello", new() { Customer = new() { ["company"] = new() { Status = "known", Value = "Invented Ltd", Evidence = "Hello" } } });
    Assert(report.Customer["company"].Status != "known");
});
Add("客户数量可从原文千分位直接核验", () =>
{
    const string source = "Brush A 5,000 pcs";
    var report = InquiryService.Validate(source, new() { Products = [new() { SourceEvidence = source, Fields = new() { ["quantity"] = new() { Status = "known", Value = "5000", Evidence = "5,000 pcs" } } }] });
    Assert(report.Products[0].Fields["quantity"].Status == "known");
});
Add("多产品数量证据不能引用另一产品，重叠范围也不能确认", () =>
{
    const string source = "Brush A 500 pcs. Brush B 800 pcs.";
    var a = new InquiryProduct { SourceEvidence = "Brush A 500 pcs.", Fields = new() { ["quantity"] = new() { Status = "known", Value = "800", Evidence = "800 pcs" } } };
    var b = new InquiryProduct { SourceEvidence = "Brush B 800 pcs.", Fields = new() { ["quantity"] = new() { Status = "known", Value = "800", Evidence = "800 pcs" } } };
    var report = InquiryService.Validate(source, new() { Products = [a, b] });
    Assert(report.Products[0].Fields["quantity"].Status == "ambiguous");
    Assert(report.Products[1].Fields["quantity"].Status == "known");
    var duplicateIds = InquiryService.Validate(source, new() { Products = [a with { Id = "unassigned" }, b with { Id = "unassigned" }] });
    Assert(duplicateIds.Products.Select(p => p.Id).Distinct().Count() == 2);
    report = InquiryService.Validate(source, new() { Products = [a with { SourceEvidence = source }, b] });
    Assert(report.Products.All(p => p.Fields["quantity"].Status != "known"));
});
Add("含运费报价才把目的地列为关键缺项", () =>
{
    var freight = InquiryService.Validate("Please quote 5,000 pcs, including freight.", new());
    var excluded = InquiryService.Validate("Please quote product price only, freight excluded.", new());
    Assert(freight.Customer["destination"].Priority == "critical");
    Assert(excluded.Customer["destination"].Priority == "optional");
});
Add("明确无 Logo 和包装不生成相关追问", () =>
{
    const string source = "Brush 500 pcs. No logo and no retail packaging.";
    var report = InquiryService.Validate(source, new() { Products = [new() { SourceEvidence = source }] });
    var fields = report.Products[0].Fields;
    Assert(fields["logo"].Status == "not_applicable" && fields["logo_artwork"].Status == "not_applicable");
    Assert(fields["packaging_design"].Status == "not_applicable");
    Assert(!report.Issues.Any(i => i.Path.Contains("logo") || i.Path.Contains("packaging")));
    report = InquiryService.Validate("No logo and no retail packaging.", new());
    Assert(report.Products.Single().Id == "unassigned");
    Assert(report.Products[0].Fields["logo"].Status == "not_applicable" && report.Products[0].Fields["packaging"].Status == "not_applicable");
});
Add("需要和不需要 Logo 同时出现不能被归为不适用", () =>
{
    const string source = "Need logo on back. No logo.";
    var report = InquiryService.Validate(source, new() { Products = [new() { SourceEvidence = source }] });
    Assert(report.Products[0].Fields["logo"].Status == "conflicting");
});
Add("模糊圣诞日期不补年份或改为出货日", () =>
{
    var report = InquiryService.Validate("Need them before Christmas.", new() { Customer = new() { ["target_time"] = new() { Status = "known", Value = "2026-12-25", Evidence = "before Christmas" } } });
    var time = report.Customer["target_time"];
    Assert(time.Status == "ambiguous" && time.Value == "before Christmas" && !time.Value.Contains("2026"));
});
Add("不适用必须有明确证据，未知状态和损坏结构拒绝", () =>
{
    var report = InquiryService.Validate("Logo on back", new() { Customer = new() { ["company"] = new() { Status = "not_applicable", Evidence = "Logo on back" } } });
    Assert(report.Customer["company"].Status == "ambiguous");
    Throws<InvalidDataException>(() => InquiryService.Validate("Hello", new() { Customer = new() { ["name"] = new() { Status = "invented" } } }));
    Throws<InvalidDataException>(() => InquiryService.Parse("{\"customer\":true}"));
});
Add("整理导出固定四区，JSON 保留原文和字段证据", () =>
{
    var report = InquiryService.Validate("Please quote brushes.", new());
    var markdown = InquiryService.Markdown(report);
    foreach (var heading in new[] { "客户信息", "分产品需求", "缺项 / 歧义 / 冲突", "建议追问" }) Assert(markdown.Contains(heading));
    var json = JsonSerializer.Serialize(report, JsonFormat.Options);
    Assert(json.Contains("source_text") && json.Contains("evidence"));
});
Add("普通设置导出移除密钥和密文，Provider 参数有效", () =>
{
    var provider = new ProviderProfile { EncryptedApiKey = "encrypted-test-only", Temperature = .3, MaxOutputTokens = 4096 };
    var safe = JsonSerializer.Serialize(provider with { EncryptedApiKey = null }, JsonFormat.Options);
    Assert(!safe.Contains("encrypted_api_key") && !safe.Contains("encrypted-test-only"));
    provider.Validate(requireConfigured: false);
    Throws<InvalidDataException>(() => (provider with { Temperature = double.NaN }).Validate(false));
    Throws<InvalidDataException>(() => (provider with { MaxOutputTokens = 1 }).Validate(false));
});

Add("普通包装不无条件追问设计，运费矛盾保持冲突", () =>
{
    const string source = "Brush 500 pcs in standard packaging.";
    var report = InquiryService.Validate(source, new() { Products = [new() { SourceEvidence = source, Fields = new() { ["packaging"] = new() { Status = "known", Value = "standard packaging", Evidence = "standard packaging" } } }] });
    Assert(!report.Issues.Any(i => i.Path.EndsWith(".packaging_design")));
    report = InquiryService.Validate("Freight included. Freight excluded.", new());
    Assert(report.Issues.Any(i => i.Path == "shipping_quote" && i.Status == "conflicting"));
});

Add("自定义专业方向可用，修改后撤销快速授权", () =>
{
    var custom = new StylePreset { Domain = "custom", CustomDomain = "Medical devices" };
    custom.Validate(); Assert(!custom.CanUseFast && custom.Contexts().Contains("Medical devices"));
    var approved = custom.Tested().ApproveFast(); Assert(approved.CanUseFast);
    Assert(!(approved with { CustomDomain = "Legal documents" }).CanUseFast);
    Throws<InvalidDataException>(() => new StylePreset { Domain = "custom" }.Validate());
    Throws<InvalidDataException>(() => new StylePreset { CustomDomain = "Unapproved free text" }.Validate());
});

Add("剪贴板清洗永不崩溃且拒绝无效文本", () =>
{
    Assert(ClipboardText.Sanitize(null) is null);
    Assert(ClipboardText.Sanitize("   ") is null);
    Assert(ClipboardText.Sanitize("") is null);
    Assert(ClipboardText.Sanitize("hello\0world") is null);
    Assert(ClipboardText.Sanitize(new string('a', 30_001)) is null);
    Assert(ClipboardText.Sanitize("  你好  ") == "你好");
    Assert(!string.IsNullOrEmpty(ClipboardText.Hash("你好")));
});

Add("自定义请求头校验并拒绝系统头", () =>
{
    new ProviderProfile { ExtraHeaders = new() { ["x-api-key"] = "abc", ["X-Title"] = "VantreLingo" } }.Validate(false);
    Throws<InvalidDataException>(() => new ProviderProfile { ExtraHeaders = new() { ["Authorization"] = "x" } }.Validate(false));
    Throws<InvalidDataException>(() => new ProviderProfile { ExtraHeaders = new() { ["bad header"] = "x" } }.Validate(false));
    Throws<InvalidDataException>(() => new ProviderProfile { ExtraHeaders = new() { ["x-ok"] = "" } }.Validate(false));
});

AddAsync("兼容网关透传 x-头并可关闭 json_object", async () =>
{
    string? seenHeader = null;
    bool hasFormat = true;
    using var http = new HttpClient(new StubHandler(async (message, token) =>
    {
        seenHeader = message.Headers.Contains("x-api-key") ? string.Join(",", message.Headers.GetValues("x-api-key")) : null;
        using var payload = JsonDocument.Parse(await message.Content!.ReadAsStringAsync(token));
        hasFormat = payload.RootElement.TryGetProperty("response_format", out _);
        var inner = "{\"translation\":\"Hello\",\"source_language\":\"zh-CN\",\"target_language\":\"en\",\"source_confident\":true,\"warnings\":[]}";
        return JsonResponse(new { choices = new[] { new { finish_reason = "stop", message = new { content = inner } } } });
    }));
    var client = new OpenAiCompatibleClient(http,
        new() { Model = "test-model", UseResponseFormat = false, ExtraHeaders = new() { ["x-api-key"] = "key-123" } }, null);
    Assert((await client.TranslateAsync(new("你好", plan), CancellationToken.None)).Translation == "Hello");
    Assert(seenHeader == "key-123");
    Assert(!hasFormat);
});

AddAsync("兼容 content 数组与 end_turn 或缺省结束原因", async () =>
{
    async Task<string> TranslateWith(object choice)
    {
        using var http = new HttpClient(new StubHandler((_, _) => Task.FromResult(JsonResponse(new { choices = new[] { choice } }))));
        return (await new OpenAiCompatibleClient(http, new() { Model = "m" }, null)
            .TranslateAsync(new("你好", plan), CancellationToken.None)).Translation;
    }
    var inner = "{\"translation\":\"Hello\",\"source_language\":\"zh-CN\",\"target_language\":\"en\",\"source_confident\":true,\"warnings\":[]}";
    Assert(await TranslateWith(new { finish_reason = "end_turn", message = new { content = inner } }) == "Hello");
    Assert(await TranslateWith(new { finish_reason = (string?)null, message = new { content = inner } }) == "Hello");
    var arrayContent = new { finish_reason = "stop", message = new { content = new object[] { new { type = "text", text = inner[..20] }, new { type = "text", text = inner[20..] } } } };
    Assert(await TranslateWith(arrayContent) == "Hello");
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
