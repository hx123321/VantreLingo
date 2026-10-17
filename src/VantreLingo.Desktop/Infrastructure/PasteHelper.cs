using System.Runtime.InteropServices;

namespace VantreLingo.Desktop.Infrastructure;





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
}
