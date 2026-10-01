using System.Runtime.InteropServices;
using System.Windows.Automation;
using VantreLingo.Core;

namespace VantreLingo.Desktop.Infrastructure;

internal enum CaptureReason
{
    Ok,
    OwnWindow,
    NoForeground,
    EditMiss,
    UiaNoFocus,
    UiaPassword,
    UiaPidMismatch,
    UiaNoPattern,
    UiaSelection,
    UiaEmpty,
    ForegroundChanged,
    Cancelled,
    Error,
}

internal sealed record CaptureOutcome(SelectionSnapshot? Snapshot, CaptureReason Reason);

internal sealed class CaptureService
{
    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    public async Task<CaptureOutcome> CaptureAsync(long operationId, CancellationToken token)
    {
        // 仅由明确热键触发；不读取剪贴板或轮询外部窗口。
        try
        {
            var foreground = GetForegroundWindow();
            _ = GetWindowThreadProcessId(foreground, out var processId);
            if (foreground == 0 || processId == 0) return new(null, CaptureReason.NoForeground);
            if (processId == Environment.ProcessId) return new(null, CaptureReason.OwnWindow);
            var outcome = await Task.Run(() => Capture(operationId, foreground, processId, token), token)
                .WaitAsync(TimeSpan.FromSeconds(3), token);
            return outcome;
        }
        catch (OperationCanceledException) { return new(null, CaptureReason.Cancelled); }
        catch (TimeoutException) { return new(null, CaptureReason.Error); }
        catch { return new(null, CaptureReason.Error); }
    }

    private static CaptureOutcome Capture(long operationId, nint foreground, uint processId, CancellationToken token)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            var native = NativeEditSelection.Capture(operationId, foreground, (int)processId);
            if (native is not null) return new(native, CaptureReason.Ok);
            AutomationElement? element;
            try { element = AutomationElement.FocusedElement; }
            catch { return new(null, CaptureReason.UiaNoFocus); }
            if (element is null) return new(null, CaptureReason.UiaNoFocus);
            try
            {
                if (element.Current.IsPassword) return new(null, CaptureReason.UiaPassword);
                if (element.Current.ProcessId != processId) return new(null, CaptureReason.UiaPidMismatch);
                if (!element.TryGetCurrentPattern(TextPattern.Pattern, out var pattern) || pattern is not TextPattern textPattern)
                    return new(null, CaptureReason.UiaNoPattern);
                string text;
                try
                {
                    var sels = textPattern.GetSelection();
                    if (sels.Length != 1) return new(null, CaptureReason.UiaSelection);
                    text = sels[0].GetText(30_001);
                }
                catch { return new(null, CaptureReason.UiaSelection); }
                catch { return new(null, CaptureReason.UiaSelection); }
                token.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(text) || text.Length > 30_000)
                    return new(null, CaptureReason.UiaEmpty);
                if (foreground != GetForegroundWindow())
                    return new(null, CaptureReason.ForegroundChanged);
                return new(new SelectionSnapshot(operationId, (int)processId, foreground,
                    element.Current.AutomationId, element.Current.ControlType.ProgrammaticName,
                    text, SelectionSnapshot.Hash(text), DateTimeOffset.UtcNow), CaptureReason.Ok);
            }
            catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or COMException)
            {
                return new(null, CaptureReason.Error);
            }
        }
        catch (OperationCanceledException) { return new(null, CaptureReason.Cancelled); }
        catch { return new(null, CaptureReason.Error); }
    }

    internal static string Describe(CaptureReason reason) => reason switch
    {
        CaptureReason.OwnWindow => "焦点在 VantreLingo 自身窗口，已忽略。",
        CaptureReason.NoForeground => "没有检测到前台窗口。",
        CaptureReason.UiaPassword => "密码框不支持读取。",
        CaptureReason.UiaPidMismatch => "焦点控件与前台窗口不是同一进程。",
        CaptureReason.UiaNoPattern => "该控件未暴露可选文本接口（浏览器/VS Code/Office 常见）。",
        CaptureReason.UiaSelection => "没有检测到明确选区（需先选中一段文本）。",
        CaptureReason.UiaEmpty => "选区为空或超长。",
        CaptureReason.ForegroundChanged => "读取期间焦点窗口发生变化，已阻止。",
        _ => "该控件不是标准原生文本框，或不支持选区读取。",
    };
}
