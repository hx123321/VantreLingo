using System.Text.Json;
using System.Text.Json.Serialization;

namespace VantreLingo.Core.Configuration;

public sealed record AppSettings
{
    public int SchemaVersion { get; init; } = 1;
    public LanguageSettings Language { get; init; } = new();
    public WritingSettings Writing { get; init; } = new();
    public HotkeySettings Hotkeys { get; init; } = new();
    public CaptureSettings Capture { get; init; } = new();
    public FreeTranslationSettings FreeTranslation { get; init; } = new();
    public PrivacySettings Privacy { get; init; } = new();
    public OcrSettings Ocr { get; init; } = new();

    public void Validate()
    {
        if (SchemaVersion != 1 || Language is null || Writing is null || Hotkeys is null || Capture is null ||
            FreeTranslation is null || Privacy is null || Ocr is null)
            throw new InvalidDataException("设置版本或结构不受支持，请保留原文件后手动修复。");
        Writing.Validate();
        if (Language.Mode is not ("smart" or "fixed"))
            throw new InvalidDataException("不支持的语言模式。");
        LanguageRoutingService.ValidateCode(Language.FirstChineseTarget, allowChinese: false);
        if (Language.LastForeignLanguage is not null)
            LanguageRoutingService.ValidateCode(Language.LastForeignLanguage, allowChinese: false);
        if (Language.FixedTarget is not null)
            LanguageRoutingService.ValidateCode(Language.FixedTarget);
        if (Language.Mode == "fixed" && Language.FixedTarget is null)
            throw new InvalidDataException("固定模式必须设置目标语言。");
        FreeTranslation.Validate();
        if (Ocr.Language is not null) LanguageRoutingService.ValidateCode(Ocr.Language);
        if (Privacy.SaveTranslationHistory || Privacy.SaveCustomerContent || Privacy.Telemetry || Privacy.AutoUpdate)
            throw new InvalidDataException("当前版本不支持历史、遥测或自动更新。");
    }
}

public sealed record LanguageSettings
{
    public string Mode { get; init; } = "smart";
    public string FirstChineseTarget { get; init; } = "en";
    public string? LastForeignLanguage { get; init; }
    public string? FixedTarget { get; init; }
}

public sealed record WritingSettings
{
    public string Mode { get; init; } = "review";
    public string DefaultPreset { get; init; } = "customer-business";
    // 原生安全写回 / 剪贴板粘贴 / 自动（原生优先，失败转粘贴）。
    public string Writeback { get; init; } = "auto";

    public void Validate()
    {
        if (Mode is not ("review" or "fast"))
            throw new InvalidDataException("写作模式必须为 review 或 fast。");
        if (Writeback is not ("native" or "paste" or "auto"))
            throw new InvalidDataException("写回方式必须为 native、paste 或 auto。");
    }
}

public sealed record CaptureSettings
{
    // 显式兼容取词：选区读取失败后，热键触发时模拟一次 Ctrl+C 并读取剪贴板。
    // 默认关闭；开启后仍只在明确热键时执行一次，不做后台监听，不恢复剪贴板。
    public bool AutoCopyFallback { get; init; }
}

public sealed record HotkeySettings
{
    public string ReadTranslate { get; init; } = "Alt+D";
    public string WriteTranslate { get; init; } = "Alt+G";
    public string ScreenshotOcr { get; init; } = "Alt+S";
    public string ClipboardTranslate { get; init; } = "Alt+C";
    public string PasteTranslation { get; init; } = "Alt+V";
    // 托盘总开关：关闭后注销全部全局热键，托盘图标变灰。
    public bool Enabled { get; init; } = true;
}

public sealed record FreeTranslationSettings
{
    // 默认热键走免费接口的引擎：google 或 microsoft。
    public string Engine { get; init; } = "google";

    public void Validate()
    {
        if (Engine is not ("google" or "microsoft"))
            throw new InvalidDataException("免费翻译引擎必须为 google 或 microsoft。");
    }
}

public sealed record OcrSettings
{
    public string? Language { get; init; }
}

public sealed record PrivacySettings
{
    public bool SaveTranslationHistory { get; init; }
    public bool SaveCustomerContent { get; init; }
    public bool Telemetry { get; init; }
    public bool AutoUpdate { get; init; }
}

public sealed record ProviderSettings
{
    public int SchemaVersion { get; init; } = 1;
    public string SelectedProviderId { get; init; } = "default";
    public List<ProviderProfile> Providers { get; init; } = [new()];

    [JsonIgnore]
    public ProviderProfile Selected => Providers.Single(p => p.Id == SelectedProviderId);

    public void Validate()
    {
        if (SchemaVersion != 1 || Providers is null || Providers.Count == 0 ||
            Providers.Any(p => p is null || string.IsNullOrWhiteSpace(p.Id)) ||
            Providers.Select(p => p.Id).Distinct(StringComparer.Ordinal).Count() != Providers.Count ||
            Providers.Count(p => p.Id == SelectedProviderId) != 1)
            throw new InvalidDataException("Provider 设置结构无效。");
        foreach (var profile in Providers)
            profile.Validate(requireConfigured: false);
    }
}

public sealed record ProviderProfile
{
    public string Id { get; init; } = "default";
    public string Name { get; init; } = "默认 Provider";
    public string Endpoint { get; init; } = "https://api.openai.com/v1/";
    public string Model { get; init; } = "";
    public int TimeoutSeconds { get; init; } = 60;
    public double? Temperature { get; init; }
    public int? MaxOutputTokens { get; init; }
    public bool UseResponseFormat { get; init; } = true;
    public Dictionary<string, string> ExtraHeaders { get; init; } = new();

    // 密钥是 DPAPI CurrentUser 加密后的 Base64，普通设置导出必须排除此字段。
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EncryptedApiKey { get; init; }

    public void Validate(bool requireConfigured = true)
    {
        _ = ChatCompletionsUri();
        if (string.IsNullOrWhiteSpace(Name) || Name.Length > 100 || Temperature is { } t && (!double.IsFinite(t) || t < 0 || t > 2) ||
            MaxOutputTokens is { } max && (max < 256 || max > 65_536)) throw new InvalidDataException("Provider 名称、温度或输出上限无效。");
        if (TimeoutSeconds is < 5 or > 300)
            throw new InvalidDataException("超时须在 5–300 秒之间。");
        if (requireConfigured && string.IsNullOrWhiteSpace(Model))
            throw new InvalidDataException("请先在设置中填写模型名称。");
        if (Model is null || Model.Length > 200 || Model.Any(char.IsControl))
            throw new InvalidDataException("模型名称无效。");
        ValidateExtraHeaders();
    }

    public void ValidateExtraHeaders()
    {
        if (ExtraHeaders is null) throw new InvalidDataException("自定义请求头无效。");
        if (ExtraHeaders.Count > 16) throw new InvalidDataException("自定义请求头最多 16 个。");
        foreach (var (key, value) in ExtraHeaders)
        {
            if (string.IsNullOrWhiteSpace(key) || key.Length > 64 || value is null || value.Length == 0 || value.Length > 2000)
                throw new InvalidDataException("自定义请求头名称或值无效。");
            foreach (var ch in key)
                if (!(ch is '-' || (ch >= 'A' && ch <= 'Z') || (ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9')))
                    throw new InvalidDataException($"自定义请求头名称无效：{key}。");
            if (value.Any(char.IsControl))
                throw new InvalidDataException($"自定义请求头值无效：{key}。");
            var lower = key.ToLowerInvariant();
            if (lower is "authorization" or "content-type" or "content-length" or "host" or "content-encoding" or "transfer-encoding")
                throw new InvalidDataException($"自定义请求头不能覆盖系统头：{key}。");
        }
    }

    public Uri ChatCompletionsUri()
    {
        if (!Uri.TryCreate(Endpoint, UriKind.Absolute, out var uri) ||
            (uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback)) ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
            throw new InvalidDataException("Endpoint 须为 HTTPS API 基础地址；本机服务允许 HTTP。不要在地址内填写凭据。");
        if (uri.AbsolutePath.TrimEnd('/').EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
            return uri;
        return new Uri(uri.AbsoluteUri.TrimEnd('/') + "/chat/completions");
    }
}

public static class JsonFormat
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
}
