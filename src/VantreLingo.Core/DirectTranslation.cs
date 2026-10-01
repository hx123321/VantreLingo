namespace VantreLingo.Core;

// 本地直译：输入与术语库 active 词条整句一致时直接返回目标文本，不调用 LLM。
// 只做精确匹配（不做分词/模糊），计划语言不符、草稿/禁用词条一律跳过，永不抛异常。
public static class DirectTranslation
{
    public static TranslationResult? TryTranslate(string text, LanguagePlan plan, GlossaryCatalog? glossary)
    {
        try
        {
            var input = text.Trim();
            if (input.Length == 0 || input.Length > 200 || glossary?.Terms is null) return null;
            foreach (var term in glossary.Terms)
            {
                if (term is null || term.Status != "active") continue;
                if (term.Direction is "forward" or "both" && EqualsTerm(term.Source, input) &&
                    PlanAllows(plan, term.SourceLanguage, term.TargetLanguage))
                    return new(term.Target, term.SourceLanguage, term.TargetLanguage, true, []);
                if (term.Direction is "reverse" or "both" && EqualsTerm(term.Target, input) &&
                    PlanAllows(plan, term.TargetLanguage, term.SourceLanguage))
                    return new(term.Source, term.TargetLanguage, term.SourceLanguage, true, []);
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    private static bool EqualsTerm(string term, string input) =>
        !string.IsNullOrWhiteSpace(term) && string.Equals(term.Trim(), input, StringComparison.OrdinalIgnoreCase);

    private static bool PlanAllows(LanguagePlan plan, string from, string to)
    {
        try
        {
            if (plan.Source != "auto" && !LanguageMatches(plan.Source, from)) return false;
            return LanguageMatches(plan.TargetFor(from), to);
        }
        catch
        {
            return false;
        }
    }

    private static bool LanguageMatches(string expected, string actual) =>
        expected.Equals(actual, StringComparison.OrdinalIgnoreCase) ||
        actual.StartsWith(expected + "-", StringComparison.OrdinalIgnoreCase) ||
        expected.StartsWith(actual + "-", StringComparison.OrdinalIgnoreCase);
}
