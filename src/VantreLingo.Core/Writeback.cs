namespace VantreLingo.Core;

public enum WritebackIntent { Read, Review, Fast }
public enum WritebackAction { Replace, Append }

public sealed record SelectionState(int ProcessId, nint TopLevelWindow, nint Control,
    int Start, int End, string Text, bool IsWritable, bool HasExpectedFocus);

public static class WritebackGate
{
    public static string? Validate(SelectionSnapshot snapshot, SelectionState current,
        WritebackIntent intent, string translation, IReadOnlyList<string> warnings)
    {
        if (intent == WritebackIntent.Read) return "阅读翻译不能修改来源。";
        if (string.IsNullOrWhiteSpace(translation) || translation.Length > 30_000 || translation.Contains('\0'))
            return "译文为空、过长或含无效字符，不能写回。";
        if (intent == WritebackIntent.Fast && warnings.Count > 0)
            return "译文存在风险，已保留原文。请主动打开工具查看并复制。";
        if (snapshot.Locator is not { } locator)
            return "该控件无法验证选区范围，请复制译文后手动使用。";
        if (current.ProcessId != snapshot.ProcessId || current.TopLevelWindow != snapshot.TopLevelWindow ||
            current.Control != locator.Control || !current.IsWritable || !current.HasExpectedFocus)
            return "原窗口、控件或焦点已变化，已阻止写回。";
        if (current.Start != locator.Start || current.End != locator.End ||
            current.Start < 0 || current.End <= current.Start || current.End > current.Text.Length ||
            SelectionSnapshot.Hash(current.Text) != locator.ControlTextHash ||
            current.Text[current.Start..current.End] != snapshot.OriginalText ||
            SelectionSnapshot.Hash(snapshot.OriginalText) != snapshot.OriginalHash)
            return "原文、选区或上下文已变化，已阻止写回。";
        return null;
    }

    // 追加在同一次选区替换内完成，不移动选区或先删除原文。
    public static string Replacement(SelectionSnapshot snapshot, string translation, WritebackAction action) =>
        action == WritebackAction.Append ? snapshot.OriginalText + translation : translation;
}

public sealed record TextDifference(string Prefix, string Removed, string Added, string Suffix)
{
    public static TextDifference Between(string original, string translation)
    {
        var prefix = 0;
        while (prefix < Math.Min(original.Length, translation.Length) && original[prefix] == translation[prefix]) prefix++;
        // 不在 UTF-16 代理对中间拆分。
        if (prefix > 0 && char.IsHighSurrogate(original[prefix - 1])) prefix--;
        var suffix = 0;
        while (suffix < Math.Min(original.Length, translation.Length) - prefix &&
            original[^(suffix + 1)] == translation[^(suffix + 1)]) suffix++;
        if (suffix > 0 && char.IsLowSurrogate(original[original.Length - suffix])) suffix--;
        return new(original[..prefix], original[prefix..(original.Length - suffix)],
            translation[prefix..(translation.Length - suffix)], original[(original.Length - suffix)..]);
    }
}
