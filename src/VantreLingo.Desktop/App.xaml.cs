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
    private MenuItem? _hotkeyToggle;
    private bool _hotkeysOn = true;
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
            DispatcherUnhandledException += (_, e) =>
            {
                try
                {
                    LogCrash(e.Exception);
                    _tool?.SetStatus("出现界面异常，已拦截未崩溃：" + e.Exception.GetType().Name + "。可继续使用，详情见 crash.log。");
                    Notify("出现界面异常，已拦截未崩溃。");
                    e.Handled = true;
                }
                catch { e.Handled = true; }
            };
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            {
                try { if (e.ExceptionObject is Exception ex) LogCrash(ex); } catch { }
            };
            TaskScheduler.UnobservedTaskException += (_, e) =>
            {
                try { LogCrash(e.Exception); e.SetObserved(); } catch { }
            };
            _hotkeys = new HotkeyMapper(
                () => Fire(() => _tool?.CaptureAndTranslateAsync(readOnly: true, useModel: false)),
                () => Fire(() => _tool?.CaptureAndTranslateAsync(readOnly: true, useModel: true)),
                () => Fire(() => _tool?.CaptureAndTranslateAsync(readOnly: false, useModel: false)),
                () => Fire(() => _tool?.CaptureAndTranslateAsync(readOnly: false, useModel: true)),
                () => Fire(() => _tool?.CaptureOcrAsync()),
                () => Fire(() => _tool?.TranslateClipboardAsync(useModel: false)),
                () => Fire(() => _tool?.TranslateClipboardAsync(useModel: true)),
                () => Fire(() => _tool?.PasteTranslationAsync()));
            _instance.Listen(() => Dispatcher.BeginInvoke(ShowTool));
            ShowTool();
            MigrateWriteHotkey();
            _hotkeysOn = Settings.Hotkeys.Enabled;
            if (_hotkeysOn)
            {
                if (!_hotkeys.TryApply(Settings.Hotkeys, out var error))
                    _tool.SetStatus(error);
            }
            RefreshTrayIcon();
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
        _hotkeyToggle = new MenuItem { Header = "启用全局快捷键", IsCheckable = true, IsChecked = _hotkeysOn };
        _hotkeyToggle.Click += (_, _) => ToggleHotkeys();
        menu.Items.Add(_hotkeyToggle);
        menu.Items.Add(new Separator());
        Add("输入翻译", ShowTool);
        Add("截图", () => Fire(() => _tool?.CaptureScreenshotAsync()));
        Add("截图取字", () => Fire(() => _tool?.CaptureOcrAsync()));
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

    private void MigrateWriteHotkey()
    {
        // 旧默认 Ctrl+Alt+G 迁移为 Alt+G（+Ctrl 自动走模型）。
        if (Settings.Hotkeys.WriteTranslate != "Ctrl+Alt+G") return;
        var migrated = Settings with { Hotkeys = Settings.Hotkeys with { WriteTranslate = "Alt+G" } };
        try { SettingsStore.Save(migrated); Settings = migrated; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private void ToggleHotkeys()
    {
        try
        {
            if (_hotkeysOn)
            {
                _hotkeys?.Suspend();
                _hotkeysOn = false;
            }
            else
            {
                if (_hotkeys is null || _tool is null) return;
                if (!_hotkeys.TryApply(Settings.Hotkeys, out var error))
                {
                    Notify("热键启用失败：" + error);
                    if (_hotkeyToggle is not null) _hotkeyToggle.IsChecked = false;
                    return;
                }
                _hotkeysOn = true;
            }
            if (_hotkeyToggle is not null) _hotkeyToggle.IsChecked = _hotkeysOn;
            var updated = Settings with { Hotkeys = Settings.Hotkeys with { Enabled = _hotkeysOn } };
            try { SettingsStore.Save(updated); Settings = updated; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            RefreshTrayIcon();
        }
        catch { }
    }

    private void RefreshTrayIcon()
    {
        try
        {
            if (_tray is null) return;
            _tray.IconSource = (ImageSource)FindResource(_hotkeysOn ? "AppIcon" : "AppIconGray");
            _tray.ToolTipText = _hotkeysOn ? "VantreLingo" : "VantreLingo（快捷键已暂停）";
            if (_hotkeyToggle is not null) _hotkeyToggle.IsChecked = _hotkeysOn;
        }
        catch { }
    }

    private static void Fire(Func<Task?> action)
    {
        try
        {
            var task = action();
            if (task is not null)
                task.ContinueWith(t => LogCrash(t.Exception?.InnerException ?? t.Exception ?? new Exception("后台任务失败。")),
                    TaskContinuationOptions.OnlyOnFaulted);
        }
        catch (Exception ex) { LogCrash(ex); }
    }

    internal static void LogCrash(Exception exception)
    {
        // 只记录异常类型与消息，不记录任何原文、译文或剪贴板内容。
        try
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VantreLingo");
            Directory.CreateDirectory(directory);
            var message = exception.Message ?? "";
            if (message.Length > 500) message = message[..500];
            File.AppendAllText(Path.Combine(directory, "crash.log"),
                $"{DateTimeOffset.UtcNow:O} {exception.GetType().FullName}: {message}{Environment.NewLine}");
        }
        catch { }
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
        // 总开关关闭时只校验不注册，保持暂停状态。
        if (_hotkeysOn && !_hotkeys!.TryApply(settings.Hotkeys, out error)) return false;
        error = "";
        try
        {
            _providerStore.Save(providers);
            SettingsStore.Save(settings with { Hotkeys = settings.Hotkeys with { Enabled = _hotkeysOn } });
        }
        catch
        {
            if (_hotkeysOn) _hotkeys!.TryApply(Settings.Hotkeys, out _);
            throw;
        }
        Settings = settings with { Hotkeys = settings.Hotkeys with { Enabled = _hotkeysOn } };
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
