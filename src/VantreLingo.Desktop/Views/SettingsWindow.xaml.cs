using System.IO;
using System.Security.Cryptography;
using System.Windows;
using VantreLingo.Desktop.Infrastructure;

namespace VantreLingo.Desktop.Views;

public partial class SettingsWindow : Window
{
    private readonly App _app;
    internal SettingsWindow(App app)
    {
        _app = app;
        InitializeComponent();
        var provider = app.Providers.Selected;
        EndpointText.Text = provider.Endpoint;
        ModelText.Text = provider.Model;
        TimeoutText.Text = provider.TimeoutSeconds.ToString();
        ReadHotkeyText.Text = app.Settings.Hotkeys.ReadTranslate;
        WriteHotkeyText.Text = app.Settings.Hotkeys.WriteTranslate;
        WritingMode.SelectedValue = app.Settings.Writing.Mode;
        Closed += (_, _) => ApiKeyText.Clear();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!int.TryParse(TimeoutText.Text, out var timeout))
                throw new InvalidDataException("超时必须为整数。");
            if (RemoveKeyCheck.IsChecked == true && ApiKeyText.Password.Length > 0)
                throw new InvalidDataException("请在填写新密钥和清除密钥之间选择一种操作。");
            var previous = _app.Providers.Selected;
            var updated = previous with
            {
                Endpoint = EndpointText.Text.Trim(),
                Model = ModelText.Text.Trim(),
                TimeoutSeconds = timeout,
                EncryptedApiKey = RemoveKeyCheck.IsChecked == true ? null :
                    ApiKeyText.Password.Length > 0 ? DpapiSecretStore.Encrypt(ApiKeyText.Password) : previous.EncryptedApiKey
            };
            var providers = _app.Providers with
            {
                Providers = _app.Providers.Providers.Select(p => p.Id == updated.Id ? updated : p).ToList()
            };
            var settings = _app.Settings with
            {
                Writing = _app.Settings.Writing with { Mode = WritingMode.SelectedValue as string ?? "review" },
                Hotkeys = _app.Settings.Hotkeys with
                {
                    ReadTranslate = ReadHotkeyText.Text.Trim(),
                    WriteTranslate = WriteHotkeyText.Text.Trim()
                },
                Language = _app.Settings.Language with
                {
                    LastForeignLanguage = ClearLanguageCheck.IsChecked == true ? null : _app.Settings.Language.LastForeignLanguage
                }
            };
            if (!_app.SaveConfiguration(settings, providers, out var error))
            {
                StatusText.Text = error;
                return;
            }
            ApiKeyText.Clear();
            Close();
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or CryptographicException)
        {
            StatusText.Text = ex is InvalidDataException ? ex.Message : "无法保存设置，请检查本地目录权限。";
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
