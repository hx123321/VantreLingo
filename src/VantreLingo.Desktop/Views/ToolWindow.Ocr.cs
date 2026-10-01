using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using VantreLingo.Desktop.Infrastructure;
namespace VantreLingo.Desktop.Views;

public partial class ToolWindow
{
    private ScreenshotSelection? _screenshot;
    private BitmapSource? _screenshotImage;
    private async void Ocr_Click(object sender, RoutedEventArgs e) => await CaptureOcrAsync();
    internal async Task CaptureOcrAsync()
    {
        // 避免热键连按打开多个框选层。
        if (_screenshot is not null) return;
        var operation = BeginOperation();
        _snapshot = null; _readOnly = false;
        try
        {
            OcrService.RequireIdentity();
            if (OcrService.InstalledLanguages().Count == 0) throw new InvalidOperationException("没有已安装的 OCR 语言。请在系统中安装能力，或手动输入文本。");
            Hide();
            _screenshot = new ScreenshotSelection();
            var accepted = _screenshot.ShowDialog();
            var image = _screenshot.Image;
            _screenshot = null;
            if (accepted != true || image is null)
            { _operations.TryPublish(operation, () => { _app.ShowTool(); SetStatus("截图已取消，没有保存或上传图像。"); }); return; }
            SetStatus("正在本地识别…");
            var text = await OcrService.RecognizeAsync(image, _app.Settings.Ocr.Language, operation.Token);
            _operations.TryPublish(operation, () =>
            {
                TranslationView(); _settingInput = true;
                try { InputText.Text = text; } finally { _settingInput = false; }
                OutputText.IsReadOnly = false;
                IntentLabel.Text = "截图取字 · 请先校对识别文本";
                _screenshotImage = image;
                CopyImageButton.IsEnabled = SaveImageButton.IsEnabled = true;
                _app.ShowTool(); SetStatus("本地 OCR 完成。识别文本可编辑，再主动选择翻译或客户整理。截图没有保存或上传，可用截图复制/另存。");
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _operations.TryPublish(operation, () =>
            {
                _app.ShowTool();
                SetStatus(ex is InvalidOperationException ? ex.Message : "本地 OCR 失败，请选择已安装语言或手动输入。没有上传截图。");
            });
        }
        finally { _screenshot = null; _operations.TryPublish(operation, () => CancelButton.IsEnabled = false); }
    }

    private void CopyImage_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_screenshotImage is null) { SetStatus("没有可用截图，请先按 Alt+S 框选。"); return; }
            Clipboard.SetImage(_screenshotImage);
            SetStatus("截图已复制到剪贴板。");
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException or IOException)
        { SetStatus("截图复制失败，剪贴板被占用，请稍后重试。"); }
        catch { SetStatus("截图复制失败，请稍后重试。"); }
    }

    private void SaveImage_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_screenshotImage is null) { SetStatus("没有可用截图，请先按 Alt+S 框选。"); return; }
            var dialog = new SaveFileDialog { Filter = "PNG 图片|*.png", FileName = "screenshot.png" };
            if (dialog.ShowDialog(this) != true) return;
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(_screenshotImage));
            using var stream = File.OpenWrite(dialog.FileName);
            encoder.Save(stream);
            SetStatus("截图已按你的选择另存。");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { SetStatus("截图另存失败，请检查文件权限。"); }
        catch { SetStatus("截图另存失败，请检查文件权限。"); }
    }
}
