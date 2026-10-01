using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using VantreLingo.Core;

namespace VantreLingo.Desktop.Infrastructure;

// 所有剪贴板访问永不抛异常：获取不到返回 null，设置失败返回 false。
// 调用方必须在 UI 线程（STA）调用；若在其他线程，内部吞掉异常同样返回 null/false。
internal static class ClipboardReader
{
    public static string? TryGetText()
    {
        try
        {
            if (!Clipboard.ContainsText()) return null;
            var raw = Clipboard.GetText();
            return ClipboardText.Sanitize(raw);
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or IOException or InvalidOperationException)
        {
            return null;
        }
        catch
        {
            return null;
        }
    }

    public static bool TrySetText(string text)
    {
        try
        {
            var safe = ClipboardText.Sanitize(text);
            if (safe is null) return false;
            Clipboard.SetText(safe);
            return true;
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or IOException or InvalidOperationException)
        {
            return false;
        }
        catch
        {
            return false;
        }
    }

    public static string? CurrentHash()
    {
        var text = TryGetText();
        return text is null ? null : ClipboardText.Hash(text);
    }
}
