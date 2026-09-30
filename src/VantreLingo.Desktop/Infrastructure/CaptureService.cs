using System.Runtime.InteropServices;
using System.Windows.Automation;
using VantreLingo.Core;

namespace VantreLingo.Desktop.Infrastructure;

internal sealed class CaptureService
{
    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    public async Task<SelectionSnapshot?> CaptureAsync(long operationId, CancellationToken token)
    {
        // 仅由明确热键触发；不读取剪贴板或轮询外部窗口。
        var foreground = GetForegroundWindow();
        _ = GetWindowThreadProcessId(foreground, out var processId);
        if (foreground == 0 || processId == 0 || processId == Environment.ProcessId) return null;
        return await Task.Run(() => Capture(operationId, foreground, processId, token), token)
            .WaitAsync(TimeSpan.FromSeconds(3), token);
    }

    private static SelectionSnapshot? Capture(long operationId, nint foreground, uint processId, CancellationToken token)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            var native = NativeEditSelection.Capture(operationId, foreground, (int)processId);
            if (native is not null) return native;
            var element = AutomationElement.FocusedElement;
            if (element is null || element.Current.IsPassword || element.Current.ProcessId != processId ||
                !element.TryGetCurrentPattern(TextPattern.Pattern, out var pattern))
                return null;
            var selections = ((TextPattern)pattern).GetSelection();
            if (selections.Length != 1) return null;
            var text = selections[0].GetText(30_001);
            token.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(text) || text.Length > 30_000 || foreground != GetForegroundWindow())
                return null;
            return new SelectionSnapshot(operationId, (int)processId, foreground,
                element.Current.AutomationId, element.Current.ControlType.ProgrammaticName,
                text, SelectionSnapshot.Hash(text), DateTimeOffset.UtcNow);
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or COMException)
        {
            return null;
        }
    }
}
