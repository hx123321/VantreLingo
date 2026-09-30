using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace VantreLingo.Desktop.Infrastructure;

internal sealed class ScreenshotSelection : Window
{
    private readonly Canvas _canvas = new() { Background = new SolidColorBrush(Color.FromArgb(25, 0, 0, 0)) };
    private readonly Rectangle _rectangle = new() { Stroke = Brushes.DeepSkyBlue, StrokeThickness = 2, Fill = new SolidColorBrush(Color.FromArgb(30, 255, 255, 255)) };
    private Point? _start;
    internal BitmapSource? Image { get; private set; }
    internal ScreenshotSelection()
    {
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; AllowsTransparency = true;
        Background = Brushes.Transparent; ShowInTaskbar = false; Topmost = true;
        Left = SystemParameters.VirtualScreenLeft; Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth; Height = SystemParameters.VirtualScreenHeight;
        SourceInitialized += (_, _) => SetWindowPos(new WindowInteropHelper(this).Handle, -1,
            GetSystemMetrics(76), GetSystemMetrics(77), GetSystemMetrics(78), GetSystemMetrics(79), 0x0010);
        Content = _canvas; _canvas.Children.Add(_rectangle);
        var tip = new TextBlock { Text = "拖动框选取字 · Esc 取消", Foreground = Brushes.White, Background = Brushes.Black, Padding = new Thickness(12) };
        Canvas.SetLeft(tip, 24); Canvas.SetTop(tip, 24); _canvas.Children.Add(tip);
        MouseLeftButtonDown += (_, e) => { _start = e.GetPosition(_canvas); _canvas.CaptureMouse(); };
        MouseMove += (_, e) => { if (_start is { } start) Draw(start, e.GetPosition(_canvas)); };
        MouseLeftButtonUp += (_, e) =>
        {
            if (_start is not { } start) return;
            var end = e.GetPosition(_canvas); _canvas.ReleaseMouseCapture();
            var a = PointToScreen(new Point(Math.Min(start.X, end.X), Math.Min(start.Y, end.Y)));
            var b = PointToScreen(new Point(Math.Max(start.X, end.X), Math.Max(start.Y, end.Y)));
            Hide();
            DwmFlush();
            // 隐藏框选层后再抓取实际像素，截图和中间图像不落盘。
            if (b.X - a.X >= 3 && b.Y - a.Y >= 3)
                Image = Capture((int)Math.Floor(a.X), (int)Math.Floor(a.Y), (int)Math.Ceiling(b.X - a.X), (int)Math.Ceiling(b.Y - a.Y));
            DialogResult = Image is not null;
        };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; DialogResult = false; } };
    }
    private void Draw(Point start, Point end)
    {
        Canvas.SetLeft(_rectangle, Math.Min(start.X, end.X)); Canvas.SetTop(_rectangle, Math.Min(start.Y, end.Y));
        _rectangle.Width = Math.Abs(start.X - end.X); _rectangle.Height = Math.Abs(start.Y - end.Y);
    }
    private static BitmapSource? Capture(int x, int y, int width, int height)
    {
        if (width <= 0 || height <= 0 || (long)width * height > 40_000_000) return null;
        var screen = GetDC(0); if (screen == 0) return null;
        var memory = CreateCompatibleDC(screen); var bitmap = CreateCompatibleBitmap(screen, width, height);
        if (memory == 0 || bitmap == 0)
        { if (bitmap != 0) DeleteObject(bitmap); if (memory != 0) DeleteDC(memory); ReleaseDC(0, screen); return null; }
        var previous = SelectObject(memory, bitmap);
        try
        {
            if (!BitBlt(memory, 0, 0, width, height, screen, x, y, 0x00CC0020 | 0x40000000)) return null;
            var image = Imaging.CreateBitmapSourceFromHBitmap(bitmap, 0, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            image.Freeze(); return image;
        }
        finally { SelectObject(memory, previous); DeleteObject(bitmap); DeleteDC(memory); ReleaseDC(0, screen); }
    }
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("dwmapi.dll")] private static extern int DwmFlush();
    [DllImport("user32.dll")] private static extern nint GetDC(nint window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint window, nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleBitmap(nint dc, int width, int height);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint value);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint value);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(nint dest, int x, int y, int width, int height, nint source, int sx, int sy, uint operation);
}
