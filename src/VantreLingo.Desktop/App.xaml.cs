using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Hardcodet.Wpf.TaskbarNotification;
using VantreLingo.Core;
using VantreLingo.Core.Configuration;
using VantreLingo.Desktop.Infrastructure;
using VantreLingo.Desktop.Views;

namespace VantreLingo.Desktop;

public partial class App : Application
{
    private SingleInstance? _instance;
    private TaskbarIcon? _tray;
    private HotkeyMapper? _hotkeys;
    private ToolWindow? _tool;
    private SettingsWindow? _settingsWindow;
    private HttpClient? _http;
    private bool _exiting;
    internal bool IsExiting => _exiting;
    internal AppSettings Settings { get; private set; } = new();
    internal PresetCatalog Presets { get; private set; } = new();
    internal GlossaryCatalog Glossary { get; private set; } = new();
    private JsonFileStore<PresetCatalog> _presetStore = null!;
    private JsonFileStore<GlossaryCatalog> _glossaryStore = null!;
    internal ProviderSettings Providers { get; private set; } = new();
    internal JsonFileStore<AppSettings> SettingsStore { get; private set; } = null!;
    private JsonFileStore<ProviderSettings> _providerStore = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            _instance = new SingleInstance();
            if (!_instance.IsFirstInstance)
            {
                _instance.SignalFirstInstance();
                Shutdown();
                return;
            }
            var dataPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VantreLingo");
            SettingsStore = new(Path.Combine(dataPath, "settings.json"), () => new(), s => s.Validate());
            _providerStore = new(Path.Combine(dataPath, "providers.json"), () => new(), p => p.Validate());
            _presetStore = new(Path.Combine(dataPath, "presets.json"), () => new(), p => p.Validate());
            _glossaryStore = new(Path.Combine(dataPath, "glossary.json"), () => new(), g => g.Validate());
            Presets = _presetStore.Load();
            Glossary = _glossaryStore.Load();
            Settings = SettingsStore.Load();
            Providers = _providerStore.Load();
            // 禁止凭据请求经重定向转发，也没有 Provider 自动切换、启动探测或轮询。
            _http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
            _tool = new ToolWindow(this, _http);
            MainWindow = _tool;
            _tray = new TaskbarIcon
            {
                IconSource = (ImageSource)FindResource("AppIcon"),
                ToolTipText = "VantreLingo",
                ContextMenu = CreateTrayMenu()
            };
            _tray.TrayMouseDoubleClick += (_, _) => ShowTool();
            _hotkeys = new HotkeyMapper(() => _ = _tool.CaptureAndTranslateAsync(readOnly: true),
                () => _ = _tool.CaptureAndTranslateAsync(readOnly: false),
                () => _ = _tool.CaptureOcrAsync());
            _instance.Listen(() => Dispatcher.BeginInvoke(ShowTool));
            ShowTool();
            if (!_hotkeys.TryApply(Settings.Hotkeys, out var error))
                _tool.SetStatus(error);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            AppMessageBox.Show("启动失败。请检查 %LocalAppData%\\VantreLingo 中的配置或文件访问权限。" +
                (ex is InvalidDataException ? "\n" + ex.Message : ""));
            ExitApplication();
        }
    }

    private ContextMenu CreateTrayMenu()
    {
        var menu = new ContextMenu();
        Add("输入翻译", ShowTool);
        Add("截图取字", () => { if (_tool is not null) _ = _tool.CaptureOcrAsync(); });
        Add("客户整理", () => { ShowTool(); _tool?.PrepareInquiry(); });
        Add("风格与术语", ShowLibrary);
        Add("设置", ShowSettings);
        menu.Items.Add(new Separator());
        Add("退出", ExitApplication);
        return menu;

        void Add(string title, Action action)
        {
            var item = new MenuItem { Header = title };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }
    }

    internal void ShowTool()
    {
        if (_tool is null || _exiting) return;
        _tool.Show();
        if (_tool.WindowState == WindowState.Minimized) _tool.WindowState = WindowState.Normal;
        _tool.Activate();
    }

    internal void Notify(string text) => _tray?.ShowBalloonTip("VantreLingo", text, BalloonIcon.Info);

    internal void ShowSettings()
    {
        if (_tool is null || _exiting) return;
        if (_settingsWindow is null)
        {
            _settingsWindow = new SettingsWindow(this) { Owner = _tool };
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        }
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    internal void ShowLibrary()
    {
        ShowSettings(); _settingsWindow?.ShowLibraryTab();
    }
    internal void SavePresets(PresetCatalog presets, bool invalidate = true)
    {
        _presetStore.Save(presets); Presets = presets;
        if (invalidate) _tool?.ConfigurationChanged();
        _tool?.RefreshStyles();
    }
    internal void SaveGlossary(GlossaryCatalog glossary)
    {
        _glossaryStore.Save(glossary); Glossary = glossary; _tool?.ConfigurationChanged();
    }
    internal void MarkPresetTested(StylePreset tested)
    {
        var current = Presets.Presets.FirstOrDefault(p => p.Id == tested.Id);
        if (current is null || current.Fingerprint() != tested.Fingerprint()) return;
        SavePresets(Presets with { Presets = Presets.Presets.Select(p => p.Id == tested.Id ? p.Tested() : p).ToList() }, invalidate: false);
    }
    internal void SelectStyle(string id)
    {
        var settings = Settings with { Writing = Settings.Writing with { DefaultPreset = id } };
        SettingsStore.Save(settings); Settings = settings;
    }

    internal bool SaveConfiguration(AppSettings settings, ProviderSettings providers, out string error)
    {
        settings.Validate();
        providers.Validate();
        HotkeyMapper.Validate(settings.Hotkeys);
        if (!_hotkeys!.TryApply(settings.Hotkeys, out error)) return false;
        try
        {
            _providerStore.Save(providers);
            SettingsStore.Save(settings);
        }
        catch
        {
            _hotkeys.TryApply(Settings.Hotkeys, out _);
            throw;
        }
        Settings = settings;
        Providers = providers;
        _tool?.ConfigurationChanged();
        return true;
    }

    internal void CommitLanguage(string language)
    {
        var updated = Settings with { Language = Settings.Language with { LastForeignLanguage = language } };
        SettingsStore.Save(updated);
        Settings = updated;
    }

    private void ExitApplication()
    {
        _exiting = true;
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _exiting = true;
        _tool?.Dispose();
        _hotkeys?.Dispose();
        _tray?.Dispose();
        _http?.Dispose();
        _instance?.Dispose();
        base.OnExit(e);
    }
}

internal static class AppMessageBox
{
    public static void Show(string text, Window? owner = null)
    {
        if (owner is not null)
            MessageBox.Show(owner, text, "VantreLingo", MessageBoxButton.OK, MessageBoxImage.Warning);
        else
            MessageBox.Show(text, "VantreLingo", MessageBoxButton.OK, MessageBoxImage.Warning);
    }
}
