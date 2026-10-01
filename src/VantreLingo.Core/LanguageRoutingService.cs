using System.Globalization;
using VantreLingo.Core.Configuration;

namespace VantreLingo.Core;

public sealed record LanguagePlan(string Source, string? ExplicitTarget, string ChineseTarget)
{
    public string TargetFor(string detectedSource) => ExplicitTarget ??
        (LanguageRoutingService.IsChinese(detectedSource) ? ChineseTarget : "zh-CN");
}

public static class LanguageRoutingService
{
    // ICU 与 Windows NLS 的枚举不同；简繁中文常用别名必须显式保留。
    private static readonly HashSet<string> KnownCodes = CultureInfo
        .GetCultures(CultureTypes.NeutralCultures | CultureTypes.SpecificCultures)
        .Select(c => c.Name).Where(n => n.Length > 0)
        .Concat(["zh-CN", "zh-TW"]).ToHashSet(StringComparer.OrdinalIgnoreCase);

    public static bool IsChinese(string code) => code.Equals("zh", StringComparison.OrdinalIgnoreCase) ||
        code.StartsWith("zh-", StringComparison.OrdinalIgnoreCase);

    public static void ValidateCode(string code, bool allowChinese = true)
    {
        if (string.IsNullOrWhiteSpace(code) ||
            !KnownCodes.Contains(code) ||
            (!allowChinese && IsChinese(code)))
            throw new InvalidDataException("请输入有效的语言代码；最近外语不能设置为中文。");
    }

    public static LanguagePlan Plan(string source, string? target, LanguageSettings settings)
    {
        if (source != "auto") ValidateCode(source);
        if (target is not null) ValidateCode(target);
        return new LanguagePlan(source, target ?? (settings.Mode == "fixed" ? settings.FixedTarget : null),
            settings.LastForeignLanguage ?? settings.FirstChineseTarget);
    }

    public static string? MemoryCandidate(LanguagePlan plan, TranslationResult result)
    {
        if (!result.SourceConfident) return null;
        if (!IsChinese(result.SourceLanguage) && IsChinese(result.TargetLanguage))
            return result.SourceLanguage;
        if (IsChinese(result.SourceLanguage) && plan.ExplicitTarget is not null && !IsChinese(result.TargetLanguage))
            return result.TargetLanguage;
        return null;
    }
}
