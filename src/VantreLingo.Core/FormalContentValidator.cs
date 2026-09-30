using System.Text.RegularExpressions;
namespace VantreLingo.Core;

public static class FormalContentValidator
{
    public static IReadOnlyList<string> FindRisks(string source, string translation)
    {
        var risks = new List<string>();
        foreach (var (pattern, message) in new[]
        {
            (@"\b(?:we|our|ours)\b|我们|我方|本公司|我們", "我方责任主体"),
            (@"\b(?:you|your|yours)\b|你们|贵方|贵公司|你們|貴方|貴公司", "对方责任主体"),
            (@"\b(?:buyer|purchaser)\b|买方|采购方|買方|採購方", "买方责任主体"),
            (@"\b(?:seller|supplier)\b|卖方|供应商|賣方|供應商", "卖方责任主体"),
            (@"\b(?:must|shall|required|obliged)\b|必须|应当|須|必須|应承担|應承擔", "强制义务"),
            (@"\b(?:may|might|can|could)\b|可以|可能|有权|有權", "许可或不确定性")
        })
        {
            var a = Regex.Matches(source, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)).Count;
            var b = Regex.Matches(translation, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)).Count;
            if (a != b) risks.Add($"正式文件的{message}可能发生变化，请人工核对。");
        }
        if (source.Count(c => c == '\n') != translation.Count(c => c == '\n')) risks.Add("正式文件的换行结构发生变化，请检查格式。");
        // 这些规则只覆盖表面迹象，绝不声称证明事实和语义等价。
        return risks;
    }
}
