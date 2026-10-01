using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using VantreLingo.Core;
using VantreLingo.Desktop.Infrastructure;

namespace VantreLingo.Desktop.Views;

// 跟随小窗：只显示译文。原文默认截 5 字，悬停显示全文；点击译文复制。
// 粘贴/重试/主窗口走全局热键（Alt+V / Alt+C）与托盘，不在这里堆按钮。
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
        TranslationBox.PreviewMouseLeftButtonUp += (_, _) =>
        {
            try
            {
                if (string.IsNullOrEmpty(TranslationBox.SelectedText)) CopyRequested?.Invoke();
            }
            catch { }
        };
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
            SetSource(sourcePreview);
            TranslationBox.Clear();
            WatchHint.Visibility = Visibility.Collapsed;
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
            StatusLabel.Text = isFast ? "快速完成" : "翻译完成 · 点击复制";
            SetSource(source);
            TranslationBox.Text = translation;
            WatchHint.Visibility = Visibility.Collapsed;
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
            if (armClipboardWatch)
            {
                _watchEnabled = true;
                _lastClipboardHash = ClipboardReader.CurrentHash();
                WatchHint.Text = "复制新文本后自动翻译，或按 Alt+C 立即翻译剪贴板。";
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

    private void SetSource(string text)
    {
        try
        {
            SourcePreview.Text = TruncateSource(text);
            SourcePreview.ToolTip = text.Length > SourcePreview.Text.Length ? text : null;
        }
        catch
        {
            SourcePreview.Text = "";
            SourcePreview.ToolTip = null;
        }
    }

    private static string TruncateSource(string text)
    {
        try
        {
            if (text.Length <= 5) return text;
            var shortText = text[..5];
            if (char.IsHighSurrogate(shortText[^1])) shortText = shortText[..^1];
            return shortText + "…";
        }
        catch { return string.Empty; }
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
            double left = pt.X + 16;
            double top = pt.Y + 20;
            if (left + Width > area.Right) left = area.Right - Width - 8;
            if (top + 260 > area.Bottom) top = area.Bottom - 270;
            if (left < area.Left) left = area.Left + 8;
            if (top < area.Top) top = area.Top + 8;
            Left = left;
            Top = top;
        }
        catch { }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);
}
