using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using VantreLingo.Core.Operations;
using VantreLingo.Desktop.Infrastructure;
namespace VantreLingo.Desktop.Views;

public partial class ToolWindow
{
    private ScreenshotSelection? _screenshot;
    private BitmapSource? _screenshotImage;
    private async void Ocr_Click(object sender, RoutedEventArgs e) => await CaptureOcrAsync();
    private async void Screenshot_Click(object sender, RoutedEventArgs e) => await CaptureScreenshotAsync();

    // 纯截图：框选即复制到剪贴板，可另存；不经过 OCR，普通 EXE 也可用，不上传图像。
    internal async Task CaptureScreenshotAsync()
    {
        if (_screenshot is not null) return;
        var operation = BeginOperation();
        _snapshot = null; _readOnly = false;
        try
        {
            var image = await CaptureScreenAsync(operation);
            if (image is null)
            { _operations.TryPublish(operation, () => { _app.ShowTool(); SetStatus("截图已取消，没有保存或上传图像。"); }); return; }
            _operations.TryPublish(operation, () =>
            {
                _screenshotImage = image;
                CopyImageButton.IsEnabled = SaveImageButton.IsEnabled = true;
                try { Clipboard.SetImage(image); } catch { }
                _app.ShowTool(); SetStatus("截图已复制到剪贴板，可另存。没有上传图像。");
            });
        }
        catch (OperationCanceledException) { }
        catch
        {
            _operations.TryPublish(operation, () =>
            {
                _app.ShowTool();
                SetStatus("截图失败，请重试。没有保存或上传图像。");
            });
        }
        finally
        {
            _screenshot = null;
            _operations.TryPublish(operation, () => CancelButton.IsEnabled = false);
            RestoreMainIfCurrent(operation);
        }
    }

    internal async Task CaptureOcrAsync()
    {
        // 避免热键连按打开多个框选层。
        if (_screenshot is not null) return;
        var operation = BeginOperation();
        _snapshot = null; _readOnly = false;
        try
        {
            var image = await CaptureScreenAsync(operation);
            if (image is null)
            { _operations.TryPublish(operation, () => { _app.ShowTool(); SetStatus("截图已取消，没有保存或上传图像。"); }); return; }
            // 截图先保留并开放复制/另存；OCR 不可用也不影响截图功能。
            _operations.TryPublish(operation, () =>
            {
                _screenshotImage = image;
                CopyImageButton.IsEnabled = SaveImageButton.IsEnabled = true;
            });
            string text;
            try
            {
                SetStatus("正在本地识别…");
                text = await OcrService.RecognizeAsync(image, _app.Settings.Ocr.Language, operation.Token);
            }
            catch (Exception ex) when (ex is InvalidOperationException)
            {
                var reason = ex.Message;
                _operations.TryPublish(operation, () =>
                {
                    TranslationView();
                    IntentLabel.Text = "截图已保留 · OCR 不可用";
                    _app.ShowTool(); SetStatus($"截图已保留，可复制/另存。OCR 不可用：{reason}");
                });
                return;
            }
            _operations.TryPublish(operation, () =>
            {
                TranslationView(); _settingInput = true;
                try { InputText.Text = text; } finally { _settingInput = false; }
                OutputText.IsReadOnly = false;
                IntentLabel.Text = "截图取字 · 请先校对识别文本";
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
        finally
        {
            _screenshot = null;
            _operations.TryPublish(operation, () => CancelButton.IsEnabled = false);
            RestoreMainIfCurrent(operation);
        }
    }

    // 全屏框选并返回图像；取消、失败或操作被取代时返回 null。永不抛异常。
    private async Task<BitmapSource?> CaptureScreenAsync(Operation operation)
    {
        if (_screenshot is not null) return null;
        ScreenshotSelection overlay;
        try
        {
            Hide();
            overlay = new ScreenshotSelection();
        }
        catch { return null; }
        _screenshot = overlay;
        try
        {
            overlay.ShowOverlay();
            var wait = overlay.WaitAsync();
            var cancelled = Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, operation.Token);
            var done = await Task.WhenAny(wait, cancelled);
            if (done != wait)
            {
                try { await cancelled; } catch (OperationCanceledException) { }
                try { overlay.Close(); } catch { }
                return null;
            }
            var image = overlay.Image;
            try { overlay.Close(); } catch { }
            bool accepted;
            try { accepted = await wait; } catch { accepted = false; }
            return accepted ? image : null;
        }
        catch { return null; }
        finally { _screenshot = null; }
    }

    private void RestoreMainIfCurrent(Operation operation)
    {
        // 本操作仍是最新时才恢复主窗口；被新操作取代则把界面留给新流程。
        try { if (ReferenceEquals(_operation, operation) && !IsVisible && !_app.IsExiting) _app.ShowTool(); }
        catch { }
    }

    private void CopyImage_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_screenshotImage is null) { SetStatus("没有可用截图，请先截图。"); return; }
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
            if (_screenshotImage is null) { SetStatus("没有可用截图，请先截图。"); return; }
            var dialog = new SaveFileDialog { Filter = "PNG 图片|*.png", FileName = "screenshot.png" };
            if (dialog.ShowDialog(this) != true) return;
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(_screenshotImage));
            using var stream = File.Create(dialog.FileName);
            encoder.Save(stream);
            SetStatus("截图已按你的选择另存。");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { SetStatus("截图另存失败，请检查文件权限。"); }
        catch { SetStatus("截图另存失败，请检查文件权限。"); }
    }
}
