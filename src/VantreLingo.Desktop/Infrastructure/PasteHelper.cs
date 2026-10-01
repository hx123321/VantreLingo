using System.Runtime.InteropServices;

namespace VantreLingo.Desktop.Infrastructure;

// 兼容粘贴/复制：仅在用户明确按下热键/按钮时调用。
// 优先由调用方使用 NativeEditSelection.Apply；无 Locator 时才走剪贴板 + Ctrl+V。
// 自动复制取词同样只在明确翻译热键后执行一次，不做后台监听，不恢复剪贴板。
// 任何失败都返回 false，永不抛异常。
internal static class PasteHelper
{
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const byte VK_CONTROL = 0x11;
    private const byte VK_C = 0x43;
    private const byte VK_V = 0x56;

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, nuint dwExtraInfo);

    public static bool SendCopy()
    {
        try
        {
            keybd_event(VK_CONTROL, 0, 0, 0);
            keybd_event(VK_C, 0, 0, 0);
            keybd_event(VK_C, 0, KEYEVENTF_KEYUP, 0);
            keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, 0);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool SendPaste()
    {
        try
        {
            keybd_event(VK_CONTROL, 0, 0, 0);
            keybd_event(VK_V, 0, 0, 0);
            keybd_event(VK_V, 0, KEYEVENTF_KEYUP, 0);
            keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, 0);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool PasteText(string translation)
    {
        try
        {
            if (!ClipboardReader.TrySetText(translation)) return false;
            return SendPaste();
        }
        catch
        {
            return false;
        }
    }
}
