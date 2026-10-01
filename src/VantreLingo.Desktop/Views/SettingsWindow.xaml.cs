using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using VantreLingo.Core.Configuration;
using VantreLingo.Desktop.Infrastructure;

namespace VantreLingo.Desktop.Views;

public partial class SettingsWindow : Window
{
    private readonly App _app;
    private ProviderSettings _draft;
    private ProviderProfile? _editing;
    private bool _loading;
    internal SettingsWindow(App app)
    {
        _app = app; _draft = app.Providers;
        InitializeComponent(); RefreshProviders(_draft.SelectedProviderId);
        ReadHotkeyText.Text = app.Settings.Hotkeys.ReadTranslate; WriteHotkeyText.Text = app.Settings.Hotkeys.WriteTranslate;
        OcrHotkeyText.Text = app.Settings.Hotkeys.ScreenshotOcr;
        ClipboardHotkeyText.Text = app.Settings.Hotkeys.ClipboardTranslate; PasteHotkeyText.Text = app.Settings.Hotkeys.PasteTranslation;
        AutoCopyCheck.IsChecked = app.Settings.Capture.AutoCopyFallback;
        FreeEngine.SelectedValue = app.Settings.FreeTranslation.Engine;
        WritingMode.SelectedValue = app.Settings.Writing.Mode;
        WritebackMode.SelectedValue = app.Settings.Writing.Writeback;
        LanguageMode.SelectedValue = app.Settings.Language.Mode; FixedTargetText.Text = app.Settings.Language.FixedTarget ?? "";
        var languages = new List<OcrChoice> { new(null, "自动：系统首选 OCR 语言") };
        try { languages.AddRange(OcrService.InstalledLanguages().Select(l => new OcrChoice(l.LanguageTag, l.DisplayName + " · " + l.LanguageTag))); }
        catch (Exception ex) { OcrStatus.Text = ex is InvalidOperationException ? ex.Message : "本地 OCR 语言能力不可用，请手动输入。"; }
        if (app.Settings.Ocr.Language is { } saved && languages.All(l => l.Code != saved)) languages.Add(new(saved, saved + " · 当前未安装"));
        OcrLanguage.ItemsSource = languages;
        OcrLanguage.SelectedItem = languages.First(l => l.Code == app.Settings.Ocr.Language);
        Closed += (_, _) => ApiKeyText.Clear();
    }
    internal void ShowLibraryTab() => SettingsTabs.SelectedIndex = 1;
    private void RefreshProviders(string id)
    {
        _loading = true; ProviderList.ItemsSource = _draft.Providers;
        ProviderList.SelectedItem = _draft.Providers.First(p => p.Id == id); _loading = false;
        Load((ProviderProfile)ProviderList.SelectedItem);
    }
    private void Load(ProviderProfile p)
    {
        _editing = p; ProviderName.Text = p.Name; EndpointText.Text = p.Endpoint; ModelText.Text = p.Model;
        TimeoutText.Text = p.TimeoutSeconds.ToString(CultureInfo.InvariantCulture);
        TemperatureText.Text = p.Temperature?.ToString(CultureInfo.InvariantCulture) ?? "";
        MaxOutputText.Text = p.MaxOutputTokens?.ToString(CultureInfo.InvariantCulture) ?? "";
        JsonModeCheck.IsChecked = p.UseResponseFormat;
        HeadersText.Text = string.Join("\n", p.ExtraHeaders.Select(kv => $"{kv.Key}: {kv.Value}"));
        ApiKeyText.Clear(); RemoveKeyCheck.IsChecked = false;
    }
    private static Dictionary<string, string> ParseHeaders(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            var index = line.IndexOf(':');
            if (index <= 0) throw new InvalidDataException("自定义请求头格式为每行 Name: Value。");
            var key = line[..index].Trim();
            var value = line[(index + 1)..].Trim();
            if (key.Length == 0 || value.Length == 0) throw new InvalidDataException("自定义请求头名称或值不能为空。");
            result[key] = value;
        }
        return result;
    }
    private void SaveDraft()
    {
        if (_editing is null) return;
        if (!int.TryParse(TimeoutText.Text, out var timeout)) throw new InvalidDataException("超时必须为整数。");
        double? temperature = null; int? maxOutput = null;
        if (!string.IsNullOrWhiteSpace(TemperatureText.Text)) temperature = double.TryParse(TemperatureText.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var t) ? t : throw new InvalidDataException("温度必须为有效数字。");
        if (!string.IsNullOrWhiteSpace(MaxOutputText.Text)) maxOutput = int.TryParse(MaxOutputText.Text, out var max) ? max : throw new InvalidDataException("输出上限必须为整数。");
        if (RemoveKeyCheck.IsChecked == true && ApiKeyText.Password.Length > 0) throw new InvalidDataException("请在填写新密钥和清除密钥之间选择一种操作。");
        var p = _editing with { Name = ProviderName.Text.Trim(), Endpoint = EndpointText.Text.Trim(), Model = ModelText.Text.Trim(), TimeoutSeconds = timeout,
            Temperature = temperature, MaxOutputTokens = maxOutput, UseResponseFormat = JsonModeCheck.IsChecked != false,
            ExtraHeaders = ParseHeaders(HeadersText.Text ?? ""),
            EncryptedApiKey = RemoveKeyCheck.IsChecked == true ? null : ApiKeyText.Password.Length > 0 ? DpapiSecretStore.Encrypt(ApiKeyText.Password) : _editing.EncryptedApiKey };
        p.Validate(requireConfigured: false);
        _draft = _draft with { Providers = _draft.Providers.Select(x => x.Id == p.Id ? p : x).ToList() }; _editing = p;
        ApiKeyText.Clear(); RemoveKeyCheck.IsChecked = false;
    }
    private void Provider_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ProviderList.SelectedItem is not ProviderProfile selected) return;
        try { SaveDraft(); Load(_draft.Providers.First(p => p.Id == selected.Id)); }
        catch (Exception ex) when (ex is InvalidDataException or CryptographicException)
        {
            StatusText.Text = ex is InvalidDataException ? ex.Message : "密钥加密失败。";
            if (_editing is not null) { _loading = true; ProviderList.SelectedItem = _draft.Providers.First(p => p.Id == _editing.Id); _loading = false; }
        }
    }
    private void NewProvider_Click(object sender, RoutedEventArgs e) => Run(() =>
    {
        SaveDraft(); var p = new ProviderProfile { Id = Guid.NewGuid().ToString("N"), Name = "新 Provider" };
        _draft = _draft with { Providers = [.. _draft.Providers, p] }; RefreshProviders(p.Id);
    });
    private void RemoveProvider_Click(object sender, RoutedEventArgs e) => Run(() =>
    {
        if (_draft.Providers.Count == 1) throw new InvalidDataException("至少保留一个 Provider。");
        _draft = _draft with { Providers = _draft.Providers.Where(p => p.Id != _editing?.Id).ToList() }; RefreshProviders(_draft.Providers[0].Id);
    });
    private void Save_Click(object sender, RoutedEventArgs e) => Run(() =>
    {
        SaveDraft();
        var providers = _draft with { SelectedProviderId = _editing!.Id };
        var settings = _app.Settings with
        {
            Writing = _app.Settings.Writing with { Mode = WritingMode.SelectedValue as string ?? "review",
                Writeback = WritebackMode.SelectedValue as string ?? "auto" },
            Hotkeys = new() { ReadTranslate = ReadHotkeyText.Text.Trim(), WriteTranslate = WriteHotkeyText.Text.Trim(), ScreenshotOcr = OcrHotkeyText.Text.Trim(),
                ClipboardTranslate = ClipboardHotkeyText.Text.Trim(), PasteTranslation = PasteHotkeyText.Text.Trim() },
            Capture = new() { AutoCopyFallback = AutoCopyCheck.IsChecked == true },
            FreeTranslation = new() { Engine = FreeEngine.SelectedValue as string ?? "google" },
            Ocr = new() { Language = (OcrLanguage.SelectedItem as OcrChoice)?.Code },
            Language = _app.Settings.Language with { Mode = LanguageMode.SelectedValue as string ?? "smart",
                FixedTarget = string.IsNullOrWhiteSpace(FixedTargetText.Text) ? null : FixedTargetText.Text.Trim(),
                LastForeignLanguage = ClearLanguageCheck.IsChecked == true ? null : _app.Settings.Language.LastForeignLanguage }
        };
        if (!_app.SaveConfiguration(settings, providers, out var error)) { StatusText.Text = error; return; }
        Close();
    });
    private void ExportSettings_Click(object sender, RoutedEventArgs e) => Run(() =>
    {
        var dialog = new SaveFileDialog { Filter = "JSON|*.json", FileName = "settings-export.json" };
        if (dialog.ShowDialog(this) != true) return;
        // 只导出已保存的元数据，不包括任何密钥、密文或客户正文。
        File.WriteAllText(dialog.FileName, JsonSerializer.Serialize(new { settings = _app.Settings,
            providers = _app.Providers with { Providers = _app.Providers.Providers.Select(p => p with { EncryptedApiKey = null }).ToList() } }, JsonFormat.Options));
        StatusText.Text = "已导出已保存的普通设置，不含 API key。";
    });
    private void Run(Action action)
    {
        try { action(); }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or CryptographicException)
        { StatusText.Text = ex is InvalidDataException ? ex.Message : "操作失败，请检查本地权限或密钥加密状态。"; }
    }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private sealed record OcrChoice(string? Code, string Label);
}
