using System.Runtime.InteropServices;
using System.Text;
using VantreLingo.Core;

namespace VantreLingo.Desktop.Infrastructure;

// 只支持标准 Unicode Win32 Edit。UIA TextPattern 没有选区替换契约，不能据此盲贴。
internal static class NativeEditSelection
{
    private const uint GetText = 0x000D, GetTextLength = 0x000E, GetSelection = 0x00B0, ReplaceSelection = 0x00C2;
    private const int MaxControlLength = 200_000;
    private const uint TimeoutMilliseconds = 250;

    internal static SelectionSnapshot? Capture(long operationId, nint foreground, int processId)
    {
        var thread = GetWindowThreadProcessId(foreground, out _);
        var gui = new GuiThreadInfo { Size = (uint)Marshal.SizeOf<GuiThreadInfo>() };
        if (thread == 0 || !GetGUIThreadInfo(thread, ref gui) || gui.Focus == 0) return null;
        var current = Read(gui.Focus, foreground, processId, reviewWindow: 0);
        if (current is null || !current.IsWritable || !current.HasExpectedFocus || current.End <= current.Start ||
            current.Start < 0 || current.End > current.Text.Length) return null;
        var text = current.Text[current.Start..current.End];
        if (string.IsNullOrWhiteSpace(text) || text.Length > 30_000) return null;
        return new(operationId, processId, foreground, null, "Win32.Edit", text,
            SelectionSnapshot.Hash(text), DateTimeOffset.UtcNow,
            new(current.Control, current.Start, current.End, SelectionSnapshot.Hash(current.Text)));
    }

    internal static string? Apply(SelectionSnapshot snapshot, string translation, WritebackIntent intent,
        WritebackAction action, IReadOnlyList<string> warnings, nint reviewWindow, out bool attempted)
    {
        attempted = false;
        if (snapshot.Locator is not { } locator)
            return "该控件仅支持审查和复制，不能安全写回。";
        var current = Read(locator.Control, snapshot.TopLevelWindow, snapshot.ProcessId,
            intent == WritebackIntent.Review ? reviewWindow : 0);
        if (current is null) return "无法重新验证原控件，已阻止写回。";
        var error = WritebackGate.Validate(snapshot, current, intent, translation, warnings);
        if (error is not null) return error;
        var replacement = WritebackGate.Replacement(snapshot, translation, action);
        if (replacement.Length > 60_000 || current.Text.Length - (current.End - current.Start) + replacement.Length > MaxControlLength)
            return "写回后的文本过长，请复制译文后手动使用。";
        if (!Send(current.Control, 0x00D5, 0, 0, out var limit) || limit == 0 ||
            (nuint)(current.Text.Length - (current.End - current.Start) + replacement.Length) > limit)
            return "原控件的长度限制不足或无法验证，请复制译文后手动使用。";
        // 发送前再读一次目标，缩短范围验证和应用之间的间隔。
        // Win32 没有跨进程的文本 compare-and-swap；并发编辑的最终边界仍需实机验证。
        var final = Read(locator.Control, snapshot.TopLevelWindow, snapshot.ProcessId,
            intent == WritebackIntent.Review ? reviewWindow : 0);
        if (final is null) return "原控件已不可用，已阻止写回。";
        error = WritebackGate.Validate(snapshot, final, intent, translation, warnings);
        if (error is not null) return error;
        var pointer = Marshal.StringToHGlobalUni(replacement);
        try
        {
            attempted = true;
            if (!Send(current.Control, ReplaceSelection, 1, pointer, out _))
                return "原控件未确认写回，请检查原文；不会自动重试。";
        }
        finally { Marshal.FreeHGlobal(pointer); }
        var after = Read(current.Control, current.TopLevelWindow, current.ProcessId,
            intent == WritebackIntent.Review ? reviewWindow : 0);
        var expected = current.Text[..current.Start] + replacement + current.Text[current.End..];
        // 超时/不确定状态不能重试或自动恢复，以免覆盖用户后续编辑。
        return after?.Text == expected ? null : "无法确认完整写回，请检查原文；不会自动重试或更新语言记忆。";
    }

    private static SelectionState? Read(nint control, nint window, int processId, nint reviewWindow)
    {
        _ = GetWindowThreadProcessId(control, out var currentProcess);
        if (currentProcess != processId || GetAncestor(control, 2) != window || !IsWindowUnicode(control)) return null;
        var className = new StringBuilder(256);
        if (GetClassName(control, className, className.Capacity) == 0 || className.ToString() != "Edit") return null;
        var style = GetWindowLongPtr(control, -16).ToInt64();
        // 密码、只读、禁用以及不支持 Unicode 的控件均不提供写回 Locator。
        if ((style & (0x20 | 0x800)) != 0 || !IsWindowEnabled(control)) return null;
        if (!Send(control, GetTextLength, 0, 0, out var length) || length > MaxControlLength) return null;
        var pointer = Marshal.AllocHGlobal(checked(((int)length + 1) * sizeof(char)));
        string text;
        try
        {
            if (!Send(control, GetText, length + 1, pointer, out var copied) || copied != length) return null;
            text = Marshal.PtrToStringUni(pointer, (int)copied)!;
        }
        finally { Marshal.FreeHGlobal(pointer); }
        var range = Marshal.AllocHGlobal(8);
        int start, end;
        try
        {
            Marshal.WriteInt32(range, -1);
            Marshal.WriteInt32(range + 4, -1);
            if (!Send(control, GetSelection, (nuint)range, range + 4, out _)) return null;
            start = Marshal.ReadInt32(range);
            end = Marshal.ReadInt32(range + 4);
        }
        finally { Marshal.FreeHGlobal(range); }
        return new(processId, window, control, start, end, text, true, HasExpectedFocus(control, window, reviewWindow));
    }

    private static bool HasExpectedFocus(nint control, nint window, nint reviewWindow)
    {
        var foreground = GetForegroundWindow();
        if (foreground != window && (reviewWindow == 0 || foreground != reviewWindow)) return false;
        var thread = GetWindowThreadProcessId(window, out _);
        var gui = new GuiThreadInfo { Size = (uint)Marshal.SizeOf<GuiThreadInfo>() };
        return thread != 0 && GetGUIThreadInfo(thread, ref gui) && gui.Focus == control && (gui.Flags & 0x1E) == 0;
    }

    private static bool Send(nint window, uint message, nuint wParam, nint lParam, out nuint result) =>
        SendMessageTimeout(window, message, wParam, lParam, 0x0001 | 0x0002 | 0x0020, TimeoutMilliseconds, out result) != 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct GuiThreadInfo
    {
        public uint Size, Flags;
        public nint Active, Focus, Capture, MenuOwner, MoveSize, Caret;
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("user32.dll")] private static extern bool GetGUIThreadInfo(uint threadId, ref GuiThreadInfo info);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll")] private static extern bool IsWindowEnabled(nint window);
    [DllImport("user32.dll")] private static extern bool IsWindowUnicode(nint window);
    [DllImport("user32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint window, StringBuilder name, int maxCount);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint window, int index);
    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", CharSet = CharSet.Unicode)]
    private static extern nint SendMessageTimeout(nint window, uint message, nuint wParam, nint lParam,
        uint flags, uint timeout, out nuint result);
}
