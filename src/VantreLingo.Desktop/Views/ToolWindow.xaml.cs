using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
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
    private Operation? _operation;
    private bool _settingInput, _readOnly, _completed;
    private SelectionSnapshot? _snapshot;
    private LanguagePlan? _languages;
    private TranslationResult? _result;
    private string _sourceText = "";
    private string[] _warnings = [];
    private bool _refreshingStyles;
    private StylePreset? _requestStyle;
    private IReadOnlyList<GlossaryMatch> _matchedTerms = [];

    internal ToolWindow(App app, HttpClient http)
    {
        _app = app;
        _http = http;
        InitializeComponent();
        RefreshStyles();
        var choices = CultureInfo.GetCultures(CultureTypes.NeutralCultures)
            .Where(c => !string.IsNullOrEmpty(c.Name))
            .Select(c => new LanguageChoice(c.Name, $"{c.NativeName} · {c.Name}"))
            .Prepend(new LanguageChoice("zh-CN", "简体中文 · zh-CN"))
            .Prepend(new LanguageChoice("zh-TW", "繁體中文 · zh-TW")).ToArray();
        SourceLanguage.ItemsSource = choices.Prepend(new("auto", "自动识别")).ToArray();
        TargetLanguage.ItemsSource = choices.Prepend(new("smart", "智能目标")).ToArray();
        SourceLanguage.SelectedIndex = TargetLanguage.SelectedIndex = 0;
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
        OutputText.TextChanged += (_, _) =>
        {
            if (!_completed) return;
            UpdateRisksAndDiff();
            UpdateActions();
            SetResultStatus();
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

    internal void RefreshStyles()
    {
        _refreshingStyles = true;
        try
        {
            StyleList.ItemsSource = _app.Presets.Presets;
            StyleList.SelectedItem = _app.Presets.Presets.FirstOrDefault(p => p.Id == _app.Settings.Writing.DefaultPreset) ?? _app.Presets.Presets[0];
        }
        finally { _refreshingStyles = false; }
    }
    private void Style_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshingStyles || StyleList.SelectedItem is not StylePreset preset) return;
        ConfigurationChanged();
        try { _app.SelectStyle(preset.Id); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { SetStatus("风格选择未能保存。当前选择仍可使用。"); }
    }
    private void Library_Click(object sender, RoutedEventArgs e) => _app.ShowLibrary();

    private void LanguageChanged(object sender, TextChangedEventArgs e) => ConfigurationChanged();
    internal void SetStatus(string text) => StatusText.Text = text;
    internal void ConfigurationChanged()
    {
        Cancel();
        ResetResult();
    }

    private Operation BeginOperation()
    {
        var next = _operations.Begin();
        _operation?.Dispose();
        _operation = next;
        ResetResult();
        ResetInquiry();
        CancelButton.IsEnabled = true;
        return next;
    }

    private void ResetResult()
    {
        _completed = false;
        _result = null;
        _languages = null;
        _warnings = [];
        OutputText.Clear();
        DiffText.Inlines.Clear();
        CopyButton.IsEnabled = ReplaceButton.IsEnabled = AppendButton.IsEnabled = false;
    }

    internal async Task CaptureAndTranslateAsync(bool readOnly)
    {
        TranslationView();
        var fast = !readOnly && _app.Settings.Writing.Mode == "fast";
        var operation = BeginOperation();
        _snapshot = null;
        _readOnly = readOnly;
        SetStatus("正在读取选区…");
        try
        {
            var snapshot = await _capture.CaptureAsync(operation.Id, operation.Token);
            if (!_operations.TryPublish(operation, () =>
            {
                if (!fast) _app.ShowTool();
                if (snapshot is null)
                {
                    Report("没有取得明确选区，或该控件不支持选区读取。请手动粘贴文本。", fast);
                    return;
                }
                _settingInput = true;
                try { InputText.Text = snapshot.OriginalText; }
                finally { _settingInput = false; }
                _snapshot = snapshot;
                OutputText.IsReadOnly = readOnly;
                IntentLabel.Text = readOnly ? "阅读翻译 · 只读" : fast ? "快速翻译" : "写作审查 · 检查后应用";
            }) || snapshot is null) return;
            await TranslateAsync(operation, fast);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _operations.TryPublish(operation, () => Report(ex is TimeoutException
                ? "选区读取超时，请手动粘贴文本。" : "无法读取选区，请手动粘贴文本。", fast));
        }
        finally { _operations.TryPublish(operation, () => CancelButton.IsEnabled = _completed); }
    }

    private async void Translate_Click(object sender, RoutedEventArgs e)
    {
        TranslationView();
        var operation = BeginOperation();
        if (_snapshot is not null) _snapshot = _snapshot with { OperationId = operation.Id };
        // 浮窗内重试始终先审查，不能从浮窗自动写回。
        await TranslateAsync(operation, fast: false);
    }

    private static string Code(ComboBox box) =>
        box.SelectedItem is LanguageChoice choice && box.Text == choice.Label ? choice.Code : box.Text.Trim();

    private async Task TranslateAsync(Operation operation, bool fast)
    {
        SetStatus("正在翻译…");
        try
        {
            var source = Code(SourceLanguage);
            var target = Code(TargetLanguage);
            var languages = LanguageRoutingService.Plan(source, target == "smart" ? null : target, _app.Settings.Language);
            var text = InputText.Text;
            var style = ((StyleList.SelectedItem as StylePreset) ?? _app.Presets.Presets[0]).Normalized();
            style.Validate();
            if (fast && !style.CanUseFast) throw new InvalidDataException("当前自定义提示未获快速授权，请先成功测试并明确授权。");
            var glossary = _app.Glossary;
            var provider = _app.Providers.Selected;
            var client = new OpenAiCompatibleClient(_http, provider, DpapiSecretStore.Decrypt(provider.EncryptedApiKey));
            var result = await client.TranslateAsync(new TranslationRequest(text, languages, style, glossary), operation.Token);
            _operations.TryPublish(operation, () =>
            {
                _languages = languages;
                _result = result;
                _sourceText = text;
                _requestStyle = style;
                _matchedTerms = glossary.Match(text, result.SourceLanguage, result.TargetLanguage, style.Contexts());
                OutputText.Text = result.Translation;
                _completed = true;
                UpdateRisksAndDiff();
                UpdateActions();
                SetResultStatus();
                if (fast)
                {
                    // 所有权临界区内校验、一次写回和提交记忆；旧请求不能产生任何副作用。
                    var error = Apply(WritebackIntent.Fast, WritebackAction.Replace);
                    if (error is not null)
                    {
                        IntentLabel.Text = "快速写回已阻止 · 可主动审查和复制";
                        Report(error, fast: true);
                    }
                }
                else
                {
                    if (_warnings.Length == 0)
                    {
                        try { _app.MarkPresetTested(style); }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { SetStatus("译文完成，但提示测试记录未能保存。"); }
                    }
                    if (_readOnly || _snapshot is null) CommitLanguage();
                }
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            var message = ex switch
            {
                InvalidDataException => ex.Message,
                TimeoutException => "Provider 请求超时，原文保持不变。",
                HttpRequestException http => http.StatusCode is { } code
                    ? $"Provider 请求失败（HTTP {(int)code}），请检查设置。"
                    : "无法连接 Provider，请检查网络和地址。",
                _ => "翻译失败，请检查设置后重试。"
            };
            _operations.TryPublish(operation, () => Report(message, fast));
        }
        finally { _operations.TryPublish(operation, () => CancelButton.IsEnabled = _completed); }
    }

    private void UpdateRisksAndDiff()
    {
        if (_result is null) return;
        _warnings = _result.Warnings.Concat(TranslationContract.FindProtectionRisks(_sourceText, OutputText.Text))
            .Concat(GlossaryCatalog.FindRisks(OutputText.Text, _matchedTerms))
            .Concat(_requestStyle?.Scene == "formal_document" ? FormalContentValidator.FindRisks(_sourceText, OutputText.Text) : [])
            .Concat(!_result.SourceConfident || !TranslationContract.HasLanguageEvidence(_sourceText)
                ? new[] { "源语言存在歧义或缺乏语言证据，请手动确认。" } : []).Distinct().ToArray();
        var diff = TextDifference.Between(_sourceText, OutputText.Text);
        DiffText.Inlines.Clear();
        DiffText.Inlines.Add(new Run(diff.Prefix));
        DiffText.Inlines.Add(new Run(diff.Removed) { Background = Brushes.MistyRose, TextDecorations = TextDecorations.Strikethrough });
        DiffText.Inlines.Add(new Run(diff.Added) { Background = Brushes.Honeydew });
        DiffText.Inlines.Add(new Run(diff.Suffix));
    }

    private void UpdateActions()
    {
        CopyButton.IsEnabled = _completed && !string.IsNullOrWhiteSpace(OutputText.Text);
        ReplaceButton.IsEnabled = AppendButton.IsEnabled = CopyButton.IsEnabled && !_readOnly && _snapshot?.Locator is not null;
    }

    private void SetResultStatus()
    {
        if (_result is null) return;
        SetStatus($"{_result.SourceLanguage} → {_result.TargetLanguage}" +
            (_warnings.Length > 0 ? "\n" + string.Join("\n", _warnings) : " · 完成，请检查后使用。") +
            (!_readOnly && _snapshot is { Locator: null } ? "\n该控件仅支持审查和复制。" : ""));
    }

    private string? Apply(WritebackIntent intent, WritebackAction action)
    {
        if (_readOnly || !_completed || _snapshot is null || _operation is null || _snapshot.OperationId != _operation.Id)
            return "当前结果没有有效的写作选区，请重新选择并翻译。";
        UpdateRisksAndDiff(); // 人工编辑后重新检查保护项。
        var error = NativeEditSelection.Apply(_snapshot, OutputText.Text, intent, action, _warnings,
            new WindowInteropHelper(this).Handle, out var attempted);
        // 已向控件发送写入时，即使超时或校验不确定，也禁止再次使用旧快照。
        if (attempted)
        {
            _snapshot = null;
            UpdateActions();
        }
        if (error is not null) return error;
        CommitLanguage(); // 只有确认完整写回后提交写作记忆。
        SetStatus(_warnings.Length == 0 ? "已完整写回原选区。" : "已完整写回原选区；" + string.Join("\n", _warnings));
        return null;
    }

    private void CommitLanguage()
    {
        if (_warnings.Length != 0 || _languages is null || _result is null) return;
        var memory = LanguageRoutingService.MemoryCandidate(_languages, _result);
        if (memory is null) return;
        try { _app.CommitLanguage(memory); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _warnings = [.. _warnings, "译文已完成，但最近外语未能保存。"];
            SetResultStatus();
        }
    }

    private void Replace_Click(object sender, RoutedEventArgs e) => ApplyReview(WritebackAction.Replace);
    private void Append_Click(object sender, RoutedEventArgs e) => ApplyReview(WritebackAction.Append);
    private void ApplyReview(WritebackAction action)
    {
        if (_operation is null) return;
        _operations.TryPublish(_operation, () =>
        {
            var error = Apply(WritebackIntent.Review, action);
            if (error is not null) SetStatus(error);
        });
    }

    private void Report(string message, bool fast)
    {
        SetStatus(message);
        if (fast) _app.Notify(message); // 托盘气泡不激活工具窗口。
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Cancel();
    private void Cancel()
    {
        _operations.Cancel();
        _completed = false;
        _snapshot = null;
        ResetInquiry();
        CopyButton.IsEnabled = ReplaceButton.IsEnabled = AppendButton.IsEnabled = CancelButton.IsEnabled = false;
        SetStatus("已取消。输入或调整语言后可重新翻译。");
    }

    private void Settings_Click(object sender, RoutedEventArgs e) => _app.ShowSettings();
    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (_operation is null || !CopyButton.IsEnabled || string.IsNullOrWhiteSpace(OutputText.Text)) return;
        _operations.TryPublish(_operation, () =>
        {
            try { Clipboard.SetText(OutputText.Text); SetStatus("译文已复制。"); }
            catch (COMException) { SetStatus("剪贴板暂时被占用，请稍后重试。"); }
        });
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_app.IsExiting) return;
        e.Cancel = true;
        Cancel();
        Hide();
    }

    public void Dispose()
    {
        _operations.Dispose();
        _operation?.Dispose();
    }
    private sealed record LanguageChoice(string Code, string Label);
}
