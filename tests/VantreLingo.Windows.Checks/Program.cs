using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using VantreLingo.Core;
using VantreLingo.Desktop.Infrastructure;

internal static class Program
{
    private static nint _fixtureEdit;
    private static readonly WindowProcedure Procedure = FixtureProcedure;
    private const string Original = "prefix 你好 suffix 你好";
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Contains("--fixture")) return Fixture();
        var checks = new List<(string Name, Action Run)>();
        var fixtures = new List<Process>();
        var failures = 0;
        void Assert(bool value) { if (!value) throw new InvalidOperationException("Assertion failed."); }
        (Process Process, nint Window, nint Edit) Start()
        {
            var process = Process.Start(new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "VantreLingo.Windows.Checks.exe"), "--fixture")
                { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true })!;
            fixtures.Add(process);
            var line = process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
            var handles = line!.Split(',');
            return (process, (nint)long.Parse(handles[0]), (nint)long.Parse(handles[1]));
        }
        try
        {
            var a = Start(); var b = Start();
            void SelectOriginal()
            {
                Assert(SetForegroundWindow(a.Window));
                SendMessage(a.Edit, 0x000C, 0, Original);
                SendMessageNumber(a.Edit, 0x00B1, 7, 9);
            }
            SelectionSnapshot Capture()
            {
                SelectOriginal();
                return NativeEditSelection.Capture(1, a.Window, a.Process.Id) ?? throw new InvalidOperationException("Native selection unavailable.");
            }
            string Text(nint window)
            { var buffer = new StringBuilder(1000); SendMessageBuffer(window, 0x000D, (nuint)buffer.Capacity, buffer); return buffer.ToString(); }
            checks.Add(("Cross-process capture preserves exact range and context", () =>
            { var s = Capture(); Assert(s.OriginalText == "你好" && s.Locator!.Start == 7 && s.Locator.End == 9); }));
            checks.Add(("Fast replacement applies once and leaves clipboard unchanged", () =>
            {
                var s = Capture(); Clipboard.SetText("new-user-clipboard");
                Assert(NativeEditSelection.Apply(s, "Hello", WritebackIntent.Fast, WritebackAction.Replace, [], 0, out var attempted) is null && attempted);
                Assert(Text(a.Edit) == "prefix Hello suffix 你好" && Clipboard.GetText() == "new-user-clipboard");
            }));
            checks.Add(("Append submits source and complete translation in one selection write", () =>
            {
                var s = Capture(); Assert(NativeEditSelection.Apply(s, "Hello", WritebackIntent.Review, WritebackAction.Append, [], 0, out _) is null);
                Assert(Text(a.Edit) == "prefix 你好Hello suffix 你好");
            }));
            checks.Add(("Focus switched to another process cannot receive or trigger writeback", () =>
            {
                var s = Capture(); Assert(SetForegroundWindow(b.Window));
                Assert(NativeEditSelection.Apply(s, "Hello", WritebackIntent.Fast, WritebackAction.Replace, [], 0, out var attempted) is not null && !attempted);
                Assert(Text(a.Edit) == Original && Text(b.Edit) == Original);
            }));
            checks.Add(("Same text at different range is blocked", () =>
            {
                var s = Capture(); SendMessageNumber(a.Edit, 0x00B1, 17, 19);
                Assert(NativeEditSelection.Apply(s, "Hello", WritebackIntent.Fast, WritebackAction.Replace, [], 0, out var attempted) is not null && !attempted);
                Assert(Text(a.Edit) == Original);
            }));
            checks.Add(("Editing text outside the selection blocks writeback", () =>
            {
                var s = Capture(); SendMessage(a.Edit, 0x000C, 0, "prefix 你好 changed"); SendMessageNumber(a.Edit, 0x00B1, 7, 9);
                Assert(NativeEditSelection.Apply(s, "Hello", WritebackIntent.Review, WritebackAction.Replace, [], 0, out var attempted) is not null && !attempted);
                Assert(Text(a.Edit) == "prefix 你好 changed");
            }));
            checks.Add(("Read intent and risk cannot send a mutation", () =>
            {
                var s = Capture(); Assert(NativeEditSelection.Apply(s, "Hello", WritebackIntent.Read, WritebackAction.Replace, [], 0, out var read) is not null && !read);
                Assert(NativeEditSelection.Apply(s, "Hello", WritebackIntent.Fast, WritebackAction.Replace, ["risk"], 0, out var risk) is not null && !risk);
                Assert(Text(a.Edit) == Original);
            }));
            checks.Add(("Control length limit is checked before mutation", () =>
            {
                var s = Capture(); SendMessageNumber(a.Edit, 0x00C5, 20, 0);
                Assert(NativeEditSelection.Apply(s, "Long translation", WritebackIntent.Fast, WritebackAction.Replace, [], 0, out var attempted) is not null && !attempted);
                SendMessageNumber(a.Edit, 0x00C5, 200000, 0);
            }));
            checks.Add(("DPAPI ciphertext binds to current user and detects tampering", () =>
            {
                const string key = "fictional-test-key";
                var encrypted = DpapiSecretStore.Encrypt(key); Assert(!encrypted.Contains(key) && DpapiSecretStore.Decrypt(encrypted) == key);
                var bytes = Convert.FromBase64String(encrypted); bytes[^1] ^= 0xff;
                try { DpapiSecretStore.Decrypt(Convert.ToBase64String(bytes)); throw new InvalidOperationException("Tampering accepted."); }
                catch (InvalidDataException) { }
            }));
            checks.Add(("Unpackaged OCR fails explicitly without network fallback", () =>
            {
                try { OcrService.RequireIdentity(); throw new InvalidOperationException("Unexpected package identity."); }
                catch (InvalidOperationException e) { Assert(e.Message.Contains("MSIX")); }
            }));
            foreach (var (name, run) in checks)
            {
                try { run(); Console.WriteLine("PASS: " + name); }
                catch (Exception e) { failures++; Console.Error.WriteLine($"FAIL: {name} ({e.GetType().Name})"); }
            }
        }
        catch (Exception e) { failures++; Console.Error.WriteLine("Fixture setup failed: " + e.GetType().Name); }
        finally { foreach (var process in fixtures) { if (!process.HasExited) process.Kill(entireProcessTree: true); process.Dispose(); } }
        Console.WriteLine($"Windows integration: {checks.Count - failures}/{checks.Count} checks passed. OS: {Environment.OSVersion}");
        return failures == 0 ? 0 : 1;
    }
    private static int Fixture()
    {
        var cls = new WindowClass { ClassName = "VantreLingoFixture", Procedure = Marshal.GetFunctionPointerForDelegate(Procedure), Instance = GetModuleHandle(null) };
        if (RegisterClass(ref cls) == 0) return 1;
        var window = CreateWindowEx(0, "VantreLingoFixture", "VantreLingo isolated test fixture", 0x10CF0000, 100, 100, 600, 200, 0, 0, 0, 0);
        var edit = CreateWindowEx(0, "EDIT", Original, 0x50010004, 20, 20, 500, 100, window, 0, 0, 0);
        if (window == 0 || edit == 0) return 1;
        _fixtureEdit = edit; SetForegroundWindow(window); SetFocus(edit);
        Console.WriteLine($"{window},{edit}"); Console.Out.Flush();
        while (GetMessage(out var message, 0, 0, 0) > 0) { TranslateMessage(ref message); DispatchMessage(ref message); }
        return 0;
    }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate nint WindowProcedure(nint window, uint message, nuint wParam, nint lParam);
    private static nint FixtureProcedure(nint window, uint message, nuint wParam, nint lParam)
    {
        var result = DefWindowProc(window, message, wParam, lParam);
        if (message == 0x0006 && (wParam & 0xffff) != 0 && _fixtureEdit != 0) SetFocus(_fixtureEdit);
        return result;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct WindowClass
    {
        public uint Style; public nint Procedure; public int ClassExtra, WindowExtra;
        public nint Instance, Icon, Cursor, Background, MenuName; public string ClassName;
    }
    [DllImport("user32.dll", EntryPoint = "RegisterClassW", CharSet = CharSet.Unicode)] private static extern ushort RegisterClass(ref WindowClass cls);
    [DllImport("user32.dll", EntryPoint = "DefWindowProcW")] private static extern nint DefWindowProc(nint window, uint message, nuint wParam, nint lParam);
    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? name);
    [StructLayout(LayoutKind.Sequential)] private struct Message
    { public nint Window; public uint Id; public nuint WParam; public nint LParam; public uint Time; public int X, Y; public uint Private; }
    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode)] private static extern nint CreateWindowEx(uint exStyle, string cls, string title, uint style, int x, int y, int w, int h, nint parent, nint menu, nint instance, nint param);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] private static extern nint SetFocus(nint window);
    [DllImport("user32.dll", EntryPoint = "SendMessageW", CharSet = CharSet.Unicode)] private static extern nint SendMessage(nint window, uint message, nuint wParam, string text);
    [DllImport("user32.dll", EntryPoint = "SendMessageW")] private static extern nint SendMessageNumber(nint window, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll", EntryPoint = "SendMessageW", CharSet = CharSet.Unicode)] private static extern nint SendMessageBuffer(nint window, uint message, nuint wParam, StringBuilder text);
    [DllImport("user32.dll", EntryPoint = "GetMessageW")] private static extern int GetMessage(out Message message, nint window, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll", EntryPoint = "DispatchMessageW")] private static extern nint DispatchMessage(ref Message message);
}
