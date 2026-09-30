using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace VantreLingo.Desktop.Infrastructure;

internal static class OcrService
{
    internal static void RequireIdentity()
    {
        uint length = 0;
        var status = GetCurrentPackageFullName(ref length, 0);
        if (status != 122) throw new InvalidOperationException("截图 OCR 需要 MSIX 包身份。请安装本项目的 MSIX 包；普通 EXE 可使用翻译和手动输入。");
    }
    internal static IReadOnlyList<Language> InstalledLanguages()
    {
        RequireIdentity();
        return OcrEngine.AvailableRecognizerLanguages.ToArray();
    }
    internal static async Task<string> RecognizeAsync(BitmapSource image, string? languageCode, CancellationToken token)
    {
        RequireIdentity();
        var installed = InstalledLanguages();
        if (installed.Count == 0) throw new InvalidOperationException("Windows 没有已安装的 OCR 语言。请在系统语言设置中安装能力，或手动输入文本。不会自动下载模型或上传截图。");
        var engine = languageCode is null ? OcrEngine.TryCreateFromUserProfileLanguages() :
            installed.FirstOrDefault(l => l.LanguageTag.Equals(languageCode, StringComparison.OrdinalIgnoreCase)) is { } language
                ? OcrEngine.TryCreateFromLanguage(language) : null;
        if (engine is null) throw new InvalidOperationException("当前 OCR 语言未安装或不可用。请在设置中选择已安装语言，或手动输入文本。不会上传截图。");
        if (image.PixelWidth > OcrEngine.MaxImageDimension || image.PixelHeight > OcrEngine.MaxImageDimension)
            throw new InvalidOperationException($"选区过大，OCR 单边最多 {OcrEngine.MaxImageDimension} 像素。请缩小截图范围。");
        token.ThrowIfCancellationRequested();
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image));
        using var memory = new System.IO.MemoryStream(); encoder.Save(memory);
        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream))
        {
            writer.WriteBytes(memory.ToArray()); await writer.StoreAsync().AsTask(token); writer.DetachStream();
        }
        stream.Seek(0);
        var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream).AsTask(token);
        using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied).AsTask(token);
        var result = await engine.RecognizeAsync(bitmap).AsTask(token);
        token.ThrowIfCancellationRequested();
        // 提供识别文本，不向 Provider 发送图像。文本只有用户主动翻译/整理时才发送。
        return result.Text;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(ref uint length, nint packageFullName);
}
