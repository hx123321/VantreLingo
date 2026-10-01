using System.Windows;
using VantreLingo.Desktop.Infrastructure;
namespace VantreLingo.Desktop.Views;

public partial class ToolWindow
{
    private ScreenshotSelection? _screenshot;
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
                _app.ShowTool(); SetStatus("本地 OCR 完成。识别文本可编辑，再主动选择翻译或客户整理。截图没有保存或上传。");
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
}
