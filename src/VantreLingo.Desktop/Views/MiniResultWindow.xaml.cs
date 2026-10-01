using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using VantreLingo.Core;
using VantreLingo.Desktop.Infrastructure;

namespace VantreLingo.Desktop.Views;

public partial class MiniResultWindow : Window
{
    public Action? CopyRequested { get; set; }
    public Action? PasteRequested { get; set; }
    public Action? ClipboardRequested { get; set; }
    public Action? OpenMainRequested { get; set; }

    public event Action<string>? ClipboardDetected;

    private readonly DispatcherTimer _watchTimer;
    private string? _lastClipboardHash;
    private bool _watchEnabled;
    private bool _hasTranslation;

    public bool HasTranslation => _hasTranslation;
    public string CurrentTranslation => TranslationBox.Text;
    public bool IsWatching => _watchEnabled && IsVisible;

    public MiniResultWindow()
    {
        InitializeComponent();
        _watchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _watchTimer.Tick += (_, _) => WatchTick();
        Closed += (_, _) => { try { _watchTimer.Stop(); } catch { } };
    }

    public void ShowWorking(string sourcePreview)
    {
        try
        {
            _hasTranslation = false;
            _watchEnabled = false;
            StatusLabel.Text = "正在翻译…";
            SourcePreview.Text = Truncate(sourcePreview);
            TranslationBox.Clear();
            WatchHint.Visibility = Visibility.Collapsed;
            CopyButton.IsEnabled = PasteButton.IsEnabled = false;
            PositionNearCursor();
            Show();
        }
        catch { }
    }

    public void ShowSuccess(string source, string translation, bool canReplace, bool isFast)
    {
        try
        {
            _hasTranslation = !string.IsNullOrWhiteSpace(translation);
            _watchEnabled = false;
            StatusLabel.Text = isFast ? "快速完成" : "翻译完成，请检查后使用";
            SourcePreview.Text = Truncate(source);
            TranslationBox.Text = translation;
            WatchHint.Visibility = Visibility.Collapsed;
            CopyButton.IsEnabled = _hasTranslation;
            PasteButton.IsEnabled = _hasTranslation && canReplace;
            PositionNearCursor();
            Show();
        }
        catch { }
    }

    public void ShowFailure(string message, bool armClipboardWatch)
    {
        try
        {
            _hasTranslation = false;
            StatusLabel.Text = message;
            TranslationBox.Clear();
            CopyButton.IsEnabled = PasteButton.IsEnabled = false;
            if (armClipboardWatch)
            {
                _watchEnabled = true;
                _lastClipboardHash = ClipboardReader.CurrentHash();
                WatchHint.Text = "选区读取失败：已进入兼容模式。复制新文本（Ctrl+C）后自动翻译，或按 Alt+C 立即翻译剪贴板。";
                WatchHint.Visibility = Visibility.Visible;
                if (!_watchTimer.IsEnabled) _watchTimer.Start();
            }
            else
            {
                _watchEnabled = false;
                WatchHint.Visibility = Visibility.Collapsed;
            }
            PositionNearCursor();
            Show();
        }
        catch { }
    }

    public void ShowStatusOnly(string message)
    {
        try
        {
            StatusLabel.Text = message;
            PositionNearCursor();
            Show();
        }
        catch { }
    }

    public void HideMini()
    {
        try
        {
            _watchEnabled = false;
            _hasTranslation = false;
            Hide();
        }
        catch { }
    }

    private void WatchTick()
    {
        try
        {
            if (!_watchEnabled || !IsVisible) return;
            var text = ClipboardReader.TryGetText();
            if (text is null) return;
            var hash = ClipboardText.Hash(text);
            if (string.IsNullOrEmpty(hash) || hash == _lastClipboardHash) return;
            _lastClipboardHash = hash;
            ClipboardDetected?.Invoke(text);
        }
        catch { }
    }

    private void PositionNearCursor()
    {
        try
        {
            if (!GetCursorPos(out var pt)) return;
            var area = SystemParameters.WorkArea;
            var left = pt.X + 16;
            var top = pt.Y + 20;
            if (left + Width > area.Right) left = area.Right - Width - 8;
            if (top + 260 > area.Bottom) top = area.Bottom - 270;
            if (left < area.Left) left = area.Left + 8;
            if (top < area.Top) top = area.Top + 8;
            Left = left;
            Top = top;
        }
        catch { }
    }

    private static string Truncate(string text)
    {
        try
        {
            if (text.Length <= 200) return text;
            return text[..200] + "…";
        }
        catch { return string.Empty; }
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        try { CopyRequested?.Invoke(); } catch { }
    }

    private void Paste_Click(object sender, RoutedEventArgs e)
    {
        try { PasteRequested?.Invoke(); } catch { }
    }

    private void Clipboard_Click(object sender, RoutedEventArgs e)
    {
        try { ClipboardRequested?.Invoke(); } catch { }
    }

    private void Main_Click(object sender, RoutedEventArgs e)
    {
        try { OpenMainRequested?.Invoke(); } catch { }
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        try { HideMini(); } catch { }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);
}
