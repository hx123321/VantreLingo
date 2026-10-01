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
    private MiniResultWindow? _mini;
    private bool _miniFailed;

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
        try
        {
            TranslationView();
            var fast = !readOnly && _app.Settings.Writing.Mode == "fast";
            var operation = BeginOperation();
            _snapshot = null;
            _readOnly = readOnly;
            _miniFailed = false;
            SetStatus("正在读取选区…");
            EnsureMini().ShowWorking("正在读取选区…");
            CaptureOutcome outcome;
            try
            {
                outcome = await _capture.CaptureAsync(operation.Id, operation.Token);
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                outcome = new(null, CaptureReason.Error);
            }
            if (outcome.Snapshot is null && outcome.Reason is not CaptureReason.Cancelled &&
                _app.Settings.Capture.AutoCopyFallback)
            {
                var fallback = await TryAutoCopyFallbackAsync(operation);
                if (fallback is not null) outcome = new(fallback, CaptureReason.Ok);
            }
            var snapshot = outcome.Snapshot;
            var reason = outcome.Reason;
            if (!_operations.TryPublish(operation, () =>
            {
                // 快捷键路径不再直接打开主窗口，统一走小悬浮窗。
                if (snapshot is null)
                {
                    var detail = CaptureService.Describe(reason);
                    Report($"没有取得明确选区：{detail}已进入剪贴板兼容模式：复制文本后自动翻译，或按 Alt+C 翻译剪贴板。", fast, armClipboardWatch: true);
                    return;
                }
                _settingInput = true;
                try { InputText.Text = snapshot.OriginalText; }
                finally { _settingInput = false; }
                _snapshot = snapshot;
                OutputText.IsReadOnly = readOnly;
                IntentLabel.Text = readOnly ? "阅读翻译 · 只读" : fast ? "快速翻译" : "写作审查 · 检查后应用";
                EnsureMini().ShowWorking(snapshot.OriginalText);
            }) || snapshot is null) return;
            await TranslateAsync(operation, fast, fromHotkey: true);
        }
        catch (OperationCanceledException) { }
        catch
        {
            // 获取不到文本也不能崩溃：统一走小窗失败提示。
            try { Report("无法读取选区，请手动粘贴文本。", fast: false, armClipboardWatch: true); } catch { }
        }
        finally
        {
            try
            {
                var op = _operation;
                if (op is not null) _operations.TryPublish(op, () => CancelButton.IsEnabled = _completed);
            }
            catch { }
        }
    }

    private async Task<SelectionSnapshot?> TryAutoCopyFallbackAsync(Operation operation)
    {
        // 显式热键触发的一次性兼容取词：记录旧剪贴板指纹，模拟 Ctrl+C 后等新内容。
        // 只读剪贴板、不写回、不恢复，避免覆盖用户新复制内容；全程永不抛异常。
        try
        {
            string? beforeHash;
            try { beforeHash = ClipboardReader.CurrentHash(); } catch { beforeHash = null; }
            try { EnsureMini().ShowWorking("选区接口无结果，正在尝试兼容复制取词…"); } catch { }
            if (!PasteHelper.SendCopy()) return null;
            var deadline = DateTimeOffset.UtcNow.AddMilliseconds(600);
            while (DateTimeOffset.UtcNow < deadline)
            {
                try { await Task.Delay(60, operation.Token); }
                catch (OperationCanceledException) { return null; }
                catch { return null; }
                string? text;
                try { text = ClipboardReader.TryGetText(); } catch { return null; }
                if (text is null) continue;
                string hash;
                try { hash = ClipboardText.Hash(text); } catch { continue; }
                if (string.IsNullOrEmpty(hash) || hash == beforeHash) continue;
                try { operation.Token.ThrowIfCancellationRequested(); } catch { return null; }
                return new SelectionSnapshot(operation.Id, 0, 0, null, "Clipboard",
                    text, SelectionSnapshot.Hash(text), DateTimeOffset.UtcNow, null);
            }
            return null;
        }
        catch { return null; }
    }

    internal async Task TranslateClipboardAsync()
    {
        // Alt+C：复制并翻译。先模拟一次 Ctrl+C 取当前选区，600ms 内出现剪贴板
        // 新内容就用它；否则回退到现有剪贴板文本；都没有则失败提示。全程不崩溃。
        try
        {
            var fresh = await TryCopyAndReadAsync();
            if (fresh is not null)
            {
                await TranslateClipboardWithTextAsync(fresh);
                return;
            }
            var text = ClipboardReader.TryGetText();
            if (text is null)
            {
                var operation = BeginOperation();
                _operations.TryPublish(operation, () => Report("没有取到新复制内容，剪贴板也没有可用文本。请先选中文本再按 Alt+C。", fast: false, armClipboardWatch: true));
                return;
            }
            await TranslateClipboardWithTextAsync(text);
        }
        catch
        {
            try { Report("复制并翻译失败，不会崩溃。请重新选中后按 Alt+C。", fast: false, armClipboardWatch: true); } catch { }
        }
    }

    private static async Task<string?> TryCopyAndReadAsync()
    {
        try
        {
            string? beforeHash;
            try { beforeHash = ClipboardReader.CurrentHash(); } catch { beforeHash = null; }
            if (!PasteHelper.SendCopy()) return null;
            var deadline = DateTimeOffset.UtcNow.AddMilliseconds(600);
            while (DateTimeOffset.UtcNow < deadline)
            {
                try { await Task.Delay(60); } catch { return null; }
                string? text;
                try { text = ClipboardReader.TryGetText(); } catch { return null; }
                if (text is null) continue;
                string hash;
                try { hash = ClipboardText.Hash(text); } catch { continue; }
                if (string.IsNullOrEmpty(hash) || hash == beforeHash) continue;
                return text;
            }
            return null;
        }
        catch { return null; }
    }

    internal async Task TranslateClipboardWithTextAsync(string text)
    {
        try
        {
            var safe = ClipboardText.Sanitize(text);
            if (safe is null)
            {
                var noop = BeginOperation();
                _operations.TryPublish(noop, () => Report("剪贴板没有可用文本，已阻止翻译。请先复制文本。", fast: false, armClipboardWatch: true));
                return;
            }
            TranslationView();
            // 快速模式失败后仍保持快速模式：这里不切换 Writing.Mode。
            var fast = _app.Settings.Writing.Mode == "fast";
            var operation = BeginOperation();
            _miniFailed = false;
            _readOnly = false;
            var snapshot = new SelectionSnapshot(operation.Id, 0, 0, null, "Clipboard",
                safe, SelectionSnapshot.Hash(safe), DateTimeOffset.UtcNow, null);
            _operations.TryPublish(operation, () =>
            {
                _settingInput = true;
                try { InputText.Text = safe; }
                finally { _settingInput = false; }
                _snapshot = snapshot;
                OutputText.IsReadOnly = false;
                IntentLabel.Text = fast ? "快速翻译 · 剪贴板兼容" : "剪贴板翻译 · 检查后应用";
                EnsureMini().ShowWorking(safe);
                SetStatus("正在翻译剪贴板…");
            });
            await TranslateAsync(operation, fast, fromHotkey: true);
        }
        catch (OperationCanceledException) { }
        catch
        {
            try { Report("剪贴板翻译失败，不会崩溃。", fast: false, armClipboardWatch: true); } catch { }
        }
        finally
        {
            try
            {
                var op = _operation;
                if (op is not null) _operations.TryPublish(op, () => CancelButton.IsEnabled = _completed);
            }
            catch { }
        }
    }

    internal async Task PasteTranslationAsync()
    {
        try
        {
            Operation? operation = _operation;
            if (operation is null || !_completed || _result is null || string.IsNullOrWhiteSpace(OutputText.Text))
            {
                EnsureMini().ShowStatusOnly("没有可用译文：请先用快捷键翻译，出现小窗译文后再按 Alt+V。");
                return;
            }
            var translation = OutputText.Text;
            string? error = null;
            _operations.TryPublish(operation, () =>
            {
                // 有合法选区时走原生安全写回；剪贴板来源（无 Locator）走兼容粘贴。
                if (_snapshot?.Locator is not null)
                {
                    error = Apply(WritebackIntent.Review, WritebackAction.Replace);
                    if (error is null)
                    {
                        EnsureMini().ShowStatusOnly("已替换原选区。");
                        SetStatus("已通过 Alt+V 替换原选区。");
                    }
                    else
                    {
                        EnsureMini().ShowFailure(error, armClipboardWatch: false);
                        SetStatus(error);
                    }
                }
                else
                {
                    try { _mini?.HideMini(); } catch { }
                    var ok = PasteHelper.PasteText(translation);
                    if (ok)
                    {
                        SetStatus("译文已放入剪贴板并发送粘贴，请检查目标位置。");
                        CommitLanguage();
                    }
                    else
                    {
                        if (ClipboardReader.TrySetText(translation))
                        {
                            EnsureMini().ShowStatusOnly("已复制译文，自动粘贴失败，请手动按 Ctrl+V。");
                            SetStatus("剪贴板已更新，自动粘贴失败，请手动粘贴。");
                        }
                        else
                        {
                            EnsureMini().ShowFailure("粘贴失败：剪贴板被占用，请稍后重试。", armClipboardWatch: false);
                        }
                    }
                }
            });
            await Task.CompletedTask;
        }
        catch
        {
            try { EnsureMini().ShowStatusOnly("粘贴失败，不会崩溃。"); } catch { }
        }
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

    private async Task TranslateAsync(Operation operation, bool fast, bool fromHotkey = false)
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
            var direct = DirectTranslation.TryTranslate(text, languages, glossary);
            TranslationResult result;
            bool isDirect;
            if (direct is not null)
            {
                result = direct;
                isDirect = true;
            }
            else
            {
                var provider = _app.Providers.Selected;
                var client = new OpenAiCompatibleClient(_http, provider, DpapiSecretStore.Decrypt(provider.EncryptedApiKey));
                result = await client.TranslateAsync(new TranslationRequest(text, languages, style, glossary), operation.Token);
                isDirect = false;
            }
            _operations.TryPublish(operation, () =>
            {
                _languages = languages;
                _result = result;
                _sourceText = text;
                _requestStyle = style;
                _matchedTerms = glossary.Match(text, result.SourceLanguage, result.TargetLanguage, style.Contexts());
                OutputText.Text = result.Translation;
                _completed = true;
                _miniFailed = false;
                UpdateRisksAndDiff();
                UpdateActions();
                SetResultStatus();
                if (isDirect) SetStatus(StatusText.Text + "\n本地术语直译，未调用模型。");
                if (fast)
                {
                    // 快速模式：只有目标为中文才尝试一次自动写回；非中文直接弹小窗，
                    // 仍保持快速模式，不切换 Writing.Mode。所有权临界区内完成副作用。
                    if (!LanguageRoutingService.IsChinese(result.TargetLanguage))
                    {
                        IntentLabel.Text = "快速模式 · 非中文已保留原文";
                        if (!isDirect && _warnings.Length == 0)
                        {
                            try { _app.MarkPresetTested(style); }
                            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { SetStatus("译文完成，但提示测试记录未能保存。"); }
                        }
                        CommitLanguage();
                        EnsureMini().ShowSuccess(text, result.Translation, canReplace: true, isFast: false);
                    }
                    else
                    {
                        // 所有权临界区内校验、一次写回和提交记忆；旧请求不能产生任何副作用。
                        // 快速失败仍保持快速模式，不切换 Writing.Mode。
                        var error = Apply(WritebackIntent.Fast, WritebackAction.Replace);
                        if (error is not null)
                        {
                            IntentLabel.Text = "快速写回已阻止 · 可主动审查和复制";
                            Report(error, fast: true, armClipboardWatch: true);
                        }
                        else if (fromHotkey)
                        {
                            try { _mini?.HideMini(); } catch { }
                        }
                    }
                }
                else
                {
                    if (!isDirect && _warnings.Length == 0)
                    {
                        try { _app.MarkPresetTested(style); }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { SetStatus("译文完成，但提示测试记录未能保存。"); }
                    }
                    if (_readOnly || _snapshot is null) CommitLanguage();
                    if (fromHotkey)
                    {
                        var canReplace = true;
                        EnsureMini().ShowSuccess(text, result.Translation, canReplace, isFast: false);
                    }
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
            _operations.TryPublish(operation, () => Report(message, fast, armClipboardWatch: fromHotkey));
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

    private MiniResultWindow EnsureMini()
    {
        if (_mini is not null) return _mini;
        var mini = new MiniResultWindow();
        mini.CopyRequested = () =>
        {
            try
            {
                if (_operation is null || !_completed || string.IsNullOrWhiteSpace(OutputText.Text)) return;
                if (ClipboardReader.TrySetText(OutputText.Text)) SetStatus("译文已复制。");
                else mini.ShowStatusOnly("剪贴板暂时被占用，请稍后重试。");
            }
            catch { }
        };
        mini.PasteRequested = () => _ = PasteTranslationAsync();
        mini.ClipboardRequested = () => _ = TranslateClipboardAsync();
        mini.OpenMainRequested = () =>
        {
            try { _app.ShowTool(); } catch { }
        };
        mini.ClipboardDetected += text =>
        {
            try
            {
                if (!_miniFailed || !(mini.IsVisible)) return;
                _ = TranslateClipboardWithTextAsync(text);
            }
            catch { }
        };
        _mini = mini;
        return mini;
    }

    private void Report(string message, bool fast, bool armClipboardWatch = false)
    {
        try
        {
            SetStatus(message);
            _miniFailed = true;
            EnsureMini().ShowFailure(message, armClipboardWatch);
            if (fast) _app.Notify(message); // 托盘气泡不激活工具窗口，快速模式保持不变。
        }
        catch { }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Cancel();
    private void Cancel()
    {
        try { _operations.Cancel(); } catch { }
        _completed = false;
        _snapshot = null;
        _miniFailed = false;
        try { _mini?.HideMini(); } catch { }
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
            try
            {
                if (ClipboardReader.TrySetText(OutputText.Text)) SetStatus("译文已复制。");
                else SetStatus("剪贴板暂时被占用，请稍后重试。");
            }
            catch { SetStatus("剪贴板暂时被占用，请稍后重试。"); }
        });
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_app.IsExiting) return;
        e.Cancel = true;
        try { Cancel(); Hide(); _mini?.HideMini(); } catch { Hide(); }
    }

    public void Dispose()
    {
        try { _operations.Dispose(); } catch { }
        try { _operation?.Dispose(); } catch { }
        try { _mini?.Close(); } catch { }
    }
    private sealed record LanguageChoice(string Code, string Label);
}
