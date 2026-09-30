using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using VantreLingo.Core;
using VantreLingo.Core.Operations;
using VantreLingo.Desktop.Infrastructure;

namespace VantreLingo.Desktop.Views;

public partial class ToolWindow : Window, IDisposable
{
    private readonly App _app;
    private readonly HttpClient _http;
    private readonly OperationCoordinator _operations = new();
    private readonly CaptureService _capture = new();
    private bool _settingInput;
    private bool _readOnly;
    private SelectionSnapshot? _snapshot;

    internal ToolWindow(App app, HttpClient http)
    {
        _app = app;
        _http = http;
        InitializeComponent();
        var choices = CultureInfo.GetCultures(CultureTypes.NeutralCultures)
            .Where(c => !string.IsNullOrEmpty(c.Name))
            .Select(c => new LanguageChoice(c.Name, $"{c.NativeName} · {c.Name}"))
            .Prepend(new LanguageChoice("zh-CN", "简体中文 · zh-CN"))
            .Prepend(new LanguageChoice("zh-TW", "繁體中文 · zh-TW")).ToArray();
        SourceLanguage.ItemsSource = choices.Prepend(new("auto", "自动识别")).ToArray();
        TargetLanguage.ItemsSource = choices.Prepend(new("smart", "智能目标")).ToArray();
        SourceLanguage.SelectedIndex = 0;
        TargetLanguage.SelectedIndex = 0;
        SourceLanguage.AddHandler(TextBox.TextChangedEvent, new TextChangedEventHandler(LanguageChanged));
        TargetLanguage.AddHandler(TextBox.TextChangedEvent, new TextChangedEventHandler(LanguageChanged));
        InputText.TextChanged += (_, _) =>
        {
            if (_settingInput) return;
            _readOnly = false;
            OutputText.IsReadOnly = false;
            IntentLabel.Text = "输入翻译";
            ConfigurationChanged();
        };
        Closing += OnClosing;
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            Cancel();
            Hide();
            e.Handled = true;
        };
    }

    private void LanguageChanged(object sender, TextChangedEventArgs e) => ConfigurationChanged();

    internal void SetStatus(string text) => StatusText.Text = text;

    internal void ConfigurationChanged()
    {
        Cancel();
        OutputText.Clear();
        CopyButton.IsEnabled = false;
        _snapshot = null;
    }

    internal async Task CaptureAndTranslateAsync(bool readOnly)
    {
        using var operation = _operations.Begin();
        _operations.TryPublish(operation, () =>
        {
            CancelButton.IsEnabled = true;
            SetStatus("正在读取选区…");
        });
        try
        {
            var snapshot = await _capture.CaptureAsync(operation.Id, operation.Token);
            if (!_operations.TryPublish(operation, () =>
            {
                _app.ShowTool();
                CopyButton.IsEnabled = false;
                OutputText.Clear();
                if (snapshot is null)
                {
                    SetStatus("没有取得明确选区，或该控件不支持选区读取。请手动粘贴文本。");
                    return;
                }
                _settingInput = true;
                try { InputText.Text = snapshot.OriginalText; }
                finally { _settingInput = false; }
                _snapshot = snapshot;
                _readOnly = readOnly;
                OutputText.IsReadOnly = readOnly;
                IntentLabel.Text = readOnly ? "阅读翻译" : "写作审查 · 可编辑并复制译文";
            }) || snapshot is null) return;
            await TranslateAsync(operation);
        }
        catch (OperationCanceledException) { }
        catch (TimeoutException)
        {
            _operations.TryPublish(operation, () =>
            {
                _app.ShowTool();
                SetStatus("选区读取超时，请手动粘贴文本。");
            });
        }
        catch (Exception)
        {
            _operations.TryPublish(operation, () => SetStatus("无法读取选区，请手动粘贴文本。"));
        }
        finally
        {
            _operations.TryPublish(operation, () => CancelButton.IsEnabled = false);
        }
    }

    private async void Translate_Click(object sender, RoutedEventArgs e)
    {
        using var operation = _operations.Begin();
        await TranslateAsync(operation);
    }

    private static string Code(ComboBox box)
    {
        if (box.SelectedItem is LanguageChoice choice && box.Text == choice.Label)
            return choice.Code;
        return box.Text.Trim();
    }

    private async Task TranslateAsync(Operation operation)
    {
        _operations.TryPublish(operation, () =>
        {
            OutputText.Clear();
            CopyButton.IsEnabled = false;
            CancelButton.IsEnabled = true;
            SetStatus("正在翻译…");
        });
        try
        {
            var source = Code(SourceLanguage);
            var target = Code(TargetLanguage);
            var languages = LanguageRoutingService.Plan(source, target == "smart" ? null : target, _app.Settings.Language);
            var text = InputText.Text;
            var provider = _app.Providers.Selected;
            var client = new OpenAiCompatibleClient(_http, provider, DpapiSecretStore.Decrypt(provider.EncryptedApiKey));
            var result = await client.TranslateAsync(new TranslationRequest(text, languages), operation.Token);
            var risks = TranslationContract.FindProtectionRisks(text, result.Translation);
            _operations.TryPublish(operation, () =>
            {
                OutputText.Text = result.Translation;
                CopyButton.IsEnabled = true;
                var warnings = result.Warnings.Concat(risks).ToList();
                // 写作审查尚未原位应用，不提交写作语言记忆；风险结果也不更新记忆。
                var memory = LanguageRoutingService.MemoryCandidate(languages, result);
                if ((_readOnly || _snapshot is null) && risks.Count == 0 && result.Warnings.Length == 0 &&
                    TranslationContract.HasLanguageEvidence(text) && memory is not null)
                {
                    try { _app.CommitLanguage(memory); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        warnings.Add("译文已完成，但最近外语未能保存。");
                    }
                }
                SetStatus($"{result.SourceLanguage} → {result.TargetLanguage}" +
                    (warnings.Count > 0 ? "\n" + string.Join("\n", warnings) : " · 完成，请检查后使用。"));
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            var message = ex switch
            {
                InvalidDataException => ex.Message,
                TimeoutException => "Provider 请求超时，请检查服务后重试。",
                HttpRequestException http => http.StatusCode is { } code
                    ? $"Provider 请求失败（HTTP {(int)code}），请检查设置。"
                    : "无法连接 Provider，请检查网络和地址。",
                _ => "翻译失败，请检查设置后重试。"
            };
            _operations.TryPublish(operation, () => SetStatus(message));
        }
        finally
        {
            _operations.TryPublish(operation, () => CancelButton.IsEnabled = false);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Cancel();

    private void Cancel()
    {
        _operations.Cancel();
        CancelButton.IsEnabled = false;
        SetStatus("输入或调整语言后可重新翻译。");
    }

    private void Settings_Click(object sender, RoutedEventArgs e) => _app.ShowSettings();

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (!CopyButton.IsEnabled || string.IsNullOrWhiteSpace(OutputText.Text)) return;
        try
        {
            Clipboard.SetText(OutputText.Text);
            SetStatus("译文已复制。");
        }
        catch (COMException) { SetStatus("剪贴板暂时被占用，请稍后重试。"); }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_app.IsExiting) return;
        e.Cancel = true;
        Cancel();
        Hide();
    }

    public void Dispose() => _operations.Dispose();
    private sealed record LanguageChoice(string Code, string Label);
}
