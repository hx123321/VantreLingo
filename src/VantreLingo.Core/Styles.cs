using System.Text.Json;
using VantreLingo.Core.Configuration;

namespace VantreLingo.Core;

public sealed record StylePreset
{
    public string Id { get; init; } = "custom";
    public string Name { get; init; } = "自定义";
    public string Scene { get; init; } = "general";
    public string Domain { get; init; } = "general";
    public string Tone { get; init; } = "neutral";
    public string Persona { get; init; } = "natural";
    public string Length { get; init; } = "preserve";
    public string Fidelity { get; init; } = "high";
    public string? CustomPrompt { get; init; }
    public bool QuickModeApproved { get; init; }
    public string? ApprovedPromptHash { get; init; }
    public string? TestedPromptHash { get; init; }

    public StylePreset Normalized() => Scene == "formal_document" ? this with { Fidelity = "strict" } : this;
    public string[] Contexts() => Domain == "oem_manufacturing" ? [Scene, Domain, "oem"] : [Scene, Domain];
    public string Fingerprint() => SelectionSnapshot.Hash(JsonSerializer.Serialize(new
        { Scene, Domain, Tone, Persona, Length, Fidelity, CustomPrompt }));
    public bool CanUseFast => string.IsNullOrWhiteSpace(CustomPrompt) ||
        (QuickModeApproved && TestedPromptHash == Fingerprint() && ApprovedPromptHash == Fingerprint());
    public StylePreset Tested() => this with { TestedPromptHash = Fingerprint() };
    public StylePreset ApproveFast()
    {
        Validate();
        if (TestedPromptHash != Fingerprint()) throw new InvalidDataException("请先用当前配置成功完成一次审查翻译。修改配置后需重新测试。");
        return this with { QuickModeApproved = true, ApprovedPromptHash = Fingerprint() };
    }
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Id) || Id.Length > 100 || string.IsNullOrWhiteSpace(Name) || Name.Length > 100 ||
            (CustomPrompt?.Length ?? 0) > 10_000) throw new InvalidDataException("风格名称、标识或自定义提示词无效。");
        Check(Scene, StyleChoices.Scenes); Check(Domain, StyleChoices.Domains);
        Check(Tone, StyleChoices.Tones); Check(Persona, StyleChoices.Personas);
        Check(Length, StyleChoices.Lengths); Check(Fidelity, StyleChoices.Fidelities);
        if (Scene == "formal_document" && (Tone == "humorous" || Persona == "lively" || Fidelity != "strict"))
            throw new InvalidDataException("正式文件必须严格忠实，不能使用幽默或活泼表达。");
        if (Fidelity == "strict" && Length is "concise" or "heavy_shorten")
            throw new InvalidDataException("严格忠实不能与压缩省略组合。");
        if (Tone == "humorous" && Persona == "literal_rigid")
            throw new InvalidDataException("幽默风趣不能与刻板直译组合。");
    }
    private static void Check(string value, IReadOnlyDictionary<string, string> choices)
    {
        if (value is null || !choices.ContainsKey(value)) throw new InvalidDataException("风格维度包含不支持的值。");
    }
}

public static class StyleChoices
{
    public static IReadOnlyDictionary<string, string> Scenes { get; } = new Dictionary<string, string>
    { ["general"] = "通用", ["customer_email"] = "客户邮件", ["instant_message"] = "即时聊天", ["technical"] = "技术说明", ["product"] = "产品说明", ["development"] = "GitHub / 开发", ["formal_document"] = "正式文件" };
    public static IReadOnlyDictionary<string, string> Domains { get; } = new Dictionary<string, string>
    { ["general"] = "通用", ["oem_manufacturing"] = "OEM / ODM", ["brush_manufacturing"] = "竹木制品 / 梳刷", ["trade"] = "外贸采购", ["packaging"] = "包装", ["cad_manufacturing"] = "3D / CAD / 制造", ["software"] = "软件开发", ["custom"] = "用户自定义" };
    public static IReadOnlyDictionary<string, string> Tones { get; } = new Dictionary<string, string>
    { ["neutral"] = "中性", ["professional_business"] = "专业商务", ["friendly_polite"] = "友好礼貌", ["serious_precise"] = "严肃严谨", ["humorous"] = "幽默风趣" };
    public static IReadOnlyDictionary<string, string> Personas { get; } = new Dictionary<string, string>
    { ["natural"] = "自然", ["direct_practical"] = "直接务实", ["gentle"] = "温和", ["lively"] = "活泼", ["literal_rigid"] = "刻板直译" };
    public static IReadOnlyDictionary<string, string> Lengths { get; } = new Dictionary<string, string>
    { ["preserve"] = "保持", ["concise"] = "精简", ["expand"] = "展开说明", ["heavy_shorten"] = "大幅压缩" };
    public static IReadOnlyDictionary<string, string> Fidelities { get; } = new Dictionary<string, string>
    { ["standard"] = "标准", ["high"] = "高忠实", ["strict"] = "严格忠实" };
}

public sealed record PresetCatalog
{
    public int SchemaVersion { get; init; } = 1;
    public List<StylePreset> Presets { get; init; } =
    [new() { Id = "customer-business", Name = "客户商务", Scene = "customer_email", Domain = "oem_manufacturing", Tone = "professional_business", Persona = "direct_practical" },
     new() { Id = "formal-document", Name = "正式文件", Scene = "formal_document", Tone = "serious_precise", Persona = "literal_rigid", Fidelity = "strict" },
     new() { Id = "friendly-chat", Name = "友好聊天", Scene = "instant_message", Tone = "friendly_polite", Fidelity = "standard" }];
    // 示例附带的冲突说明不能覆盖内置校验规则。
    public string[][] Conflicts { get; init; } = [];
    public void Validate()
    {
        if (SchemaVersion != 1 || Presets is null || Presets.Count is < 1 or > 200 || Presets.Any(p => p is null) ||
            Presets.Select(p => p.Id).Distinct(StringComparer.Ordinal).Count() != Presets.Count)
            throw new InvalidDataException("风格库结构或标识无效。");
        foreach (var preset in Presets) preset.Validate();
    }
    public PresetCatalog Imported() => this with { Presets = Presets.Select(p => p with
        { QuickModeApproved = false, ApprovedPromptHash = null, TestedPromptHash = null }).ToList() };
}
