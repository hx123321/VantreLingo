using System.Text.RegularExpressions;

namespace VantreLingo.Core;

public sealed record GlossaryTerm
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Status { get; init; } = "draft";
    public string SourceLanguage { get; init; } = "zh-CN";
    public string TargetLanguage { get; init; } = "en";
    public string Source { get; init; } = "";
    public string Target { get; init; } = "";
    public string[] Contexts { get; init; } = [];
    public string Direction { get; init; } = "forward";
    public string[] ForbiddenVariants { get; init; } = [];
    public string[] AllowedVariants { get; init; } = [];
    public string Notes { get; init; } = "";
    public void Validate()
    {
        LanguageRoutingService.ValidateCode(SourceLanguage); LanguageRoutingService.ValidateCode(TargetLanguage);
        if (string.IsNullOrWhiteSpace(Id) || Id.Length > 100 || Status is not ("draft" or "active" or "disabled") ||
            Direction is not ("forward" or "reverse" or "both") || string.IsNullOrWhiteSpace(Source) || string.IsNullOrWhiteSpace(Target) ||
            Source.Length > 200 || Target.Length > 200 || Contexts is null || ForbiddenVariants is null || AllowedVariants is null ||
            Contexts.Concat(ForbiddenVariants).Concat(AllowedVariants).Any(v => string.IsNullOrWhiteSpace(v) || v.Length > 200) ||
            Contexts.Length > 50 || ForbiddenVariants.Length > 100 || AllowedVariants.Length > 100 || Notes is null || Notes.Length > 2000)
            throw new InvalidDataException("术语词条的状态、方向、文字或变体无效。");
        if (ForbiddenVariants.Intersect(AllowedVariants.Append(Target), StringComparer.OrdinalIgnoreCase).Any())
            throw new InvalidDataException("允许的术语与禁用变体冲突。");
    }
}

public sealed record GlossaryMatch(GlossaryTerm Term, string Source, string Target, int Start, int Length, bool Reversed);

public sealed record GlossaryCatalog
{
    public int SchemaVersion { get; init; } = 1;
    public List<GlossaryTerm> Terms { get; init; } = [];
    public void Validate()
    {
        if (SchemaVersion != 1 || Terms is null || Terms.Count > 2000 || Terms.Any(t => t is null) ||
            Terms.Select(t => t.Id).Distinct(StringComparer.Ordinal).Count() != Terms.Count)
            throw new InvalidDataException("术语库结构、版本或标识无效。");
        foreach (var term in Terms) term.Validate();
        var rules = Terms.Where(t => t.Status == "active").SelectMany(Expand).ToArray();
        foreach (var group in rules.GroupBy(r => r.Source, StringComparer.OrdinalIgnoreCase))
        {
            var entries = group.ToArray();
            for (var i = 0; i < entries.Length; i++)
                for (var j = i + 1; j < entries.Length; j++)
                {
                var a = entries[i]; var b = entries[j];
                if ((LanguageMatches(a.From, b.From) || LanguageMatches(b.From, a.From)) &&
                    (LanguageMatches(a.To, b.To) || LanguageMatches(b.To, a.To)) && a.Target != b.Target &&
                    (a.Term.Contexts.Length == 0 || b.Term.Contexts.Length == 0 || a.Term.Contexts.Intersect(b.Term.Contexts, StringComparer.OrdinalIgnoreCase).Any()))
                    throw new InvalidDataException("相同语言、方向和重叠上下文存在冲突的 active 术语，请先禁用旧词。");
                }
        }
    }
    public IReadOnlyList<GlossaryMatch> Candidates(string text, LanguagePlan plan, IEnumerable<string> contexts)
    {
        return Terms.Where(t => t.Status == "active").SelectMany(Expand)
            .Where(r => (plan.Source == "auto" || LanguageMatches(r.From, plan.Source)) && LanguageMatches(r.To, plan.TargetFor(r.From)))
            .Select(r => (r.From, r.To)).Distinct()
            .SelectMany(pair => Match(text, pair.From, pair.To, contexts)).ToArray();
    }
    private sealed record Rule(GlossaryTerm Term, string From, string To, string Source, string Target, bool Reversed);
    private static IEnumerable<Rule> Expand(GlossaryTerm t)
    {
        if (t.Direction is "forward" or "both") yield return new(t, t.SourceLanguage, t.TargetLanguage, t.Source, t.Target, false);
        if (t.Direction is "reverse" or "both") yield return new(t, t.TargetLanguage, t.SourceLanguage, t.Target, t.Source, true);
    }
    private static bool LanguageMatches(string expected, string actual) => expected.Equals(actual, StringComparison.OrdinalIgnoreCase) ||
        (!expected.Contains('-') && actual.StartsWith(expected + "-", StringComparison.OrdinalIgnoreCase));
    public IReadOnlyList<GlossaryMatch> Match(string text, string sourceLanguage, string targetLanguage, IEnumerable<string> contexts)
    {
        Validate();
        var scope = contexts.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidates = new List<GlossaryMatch>();
        foreach (var rule in Terms.Where(t => t.Status == "active").SelectMany(Expand))
        {
            if (!LanguageMatches(rule.From, sourceLanguage) || !LanguageMatches(rule.To, targetLanguage) ||
                (rule.Term.Contexts.Length > 0 && !rule.Term.Contexts.Any(scope.Contains))) continue;
            foreach (Match match in Regex.Matches(text, BoundaryPattern(rule.Source), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
                candidates.Add(new(rule.Term, rule.Source, rule.Target, match.Index, match.Length, rule.Reversed));
        }
        var selected = new List<GlossaryMatch>();
        foreach (var match in candidates.OrderByDescending(m => m.Length).ThenBy(m => m.Start).ThenBy(m => m.Term.Id, StringComparer.Ordinal))
            if (!selected.Any(m => match.Start < m.Start + m.Length && m.Start < match.Start + match.Length)) selected.Add(match);
        return selected.OrderBy(m => m.Start).ToArray();
    }
    public static string BoundaryPattern(string phrase)
    {
        static bool Latin(char c) => c <= 0x7f && (char.IsLetterOrDigit(c) || c == '_');
        return (Latin(phrase[0]) ? @"(?<![\p{L}\p{N}_])" : "") + Regex.Escape(phrase) +
            (Latin(phrase[^1]) ? @"(?![\p{L}\p{N}_])" : "");
    }
    public static IReadOnlyList<string> FindRisks(string translated, IReadOnlyList<GlossaryMatch> matches)
    {
        var risks = new List<string>();
        foreach (var group in matches.GroupBy(m => (m.Term.Id, m.Reversed)))
        {
            var match = group.First();
            var variants = (match.Reversed ? new[] { match.Target } : match.Term.AllowedVariants.Prepend(match.Target)).Distinct().ToArray();
            var hits = variants.SelectMany(v => Regex.Matches(translated, BoundaryPattern(v), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)).Cast<Match>())
                .Select(m => (m.Index, m.Length)).Distinct().Count();
            if (hits < group.Count()) risks.Add($"术语未按要求保留：{match.Source} → {match.Target}");
            if (!match.Reversed && match.Term.ForbiddenVariants.Any(v => Regex.IsMatch(translated, BoundaryPattern(v), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1))))
                risks.Add($"译文含术语的禁用变体：{match.Source}");
        }
        return risks;
    }
}
