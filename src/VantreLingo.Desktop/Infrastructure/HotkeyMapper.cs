// 从固定 STranslate Helpers/HotkeyMapper.cs 传统热键注册分支裁剪派生。
// Copyright © 2022 zggsong. MIT 许可证见 licenses/STranslate.MIT.txt。
// 移除 ChefKeys、低级键鼠钩子、按住键、Ctrl+CC、Ioc、正文日志及后台重试。
using System.ComponentModel;
using System.IO;
using System.Windows.Input;
using NHotkey;
using NHotkey.Wpf;
using VantreLingo.Core.Configuration;

namespace VantreLingo.Desktop.Infrastructure;

internal sealed class HotkeyMapper(Action read, Action review) : IDisposable
{
    private HotkeySettings _current = new() { ReadTranslate = "", WriteTranslate = "" };

    public bool TryApply(HotkeySettings settings, out string error)
    {
        try
        {
            Validate(settings);
            Remove();
            Register(settings);
            _current = settings;
            error = "";
            return true;
        }
        catch (Exception ex) when (ex is InvalidDataException or HotkeyAlreadyRegisteredException or ArgumentException or Win32Exception)
        {
            Remove();
            try { Register(_current); }
            catch (HotkeyAlreadyRegisteredException)
            {
                error = "热键冲突，原热键也暂时不可用；请修改后重新保存。";
                return false;
            }
            error = ex is InvalidDataException ? ex.Message : "热键已被其他程序占用，请修改后重新保存。";
            return false;
        }
    }

    public static void Validate(HotkeySettings settings)
    {
        var read = Parse(settings.ReadTranslate);
        var write = Parse(settings.WriteTranslate);
        if (read is not null && write is not null && read.Value.Equals(write.Value))
            throw new InvalidDataException("阅读和审查热键不能相同。");
    }

    private static HotkeyModel? Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var model = new HotkeyModel(text);
        if (!model.Validate(validateKeyGestrue: true) ||
            (model.ModifierKeys == ModifierKeys.Control && model.CharKey is Key.C or Key.V or Key.A))
            throw new InvalidDataException("热键无效，或占用了 Ctrl+C / Ctrl+V / Ctrl+A 等基础编辑键。");
        return model;
    }

    private void Register(HotkeySettings settings)
    {
        Add("VantreLingo.Read", settings.ReadTranslate, read);
        Add("VantreLingo.Review", settings.WriteTranslate, review);
        // Alt+S 仅保留在配置中；OCR 接通前不占用热键。
    }

    private static void Add(string id, string text, Action action)
    {
        if (Parse(text) is not { } hotkey) return;
        HotkeyManager.Current.AddOrReplace(id, hotkey.CharKey, hotkey.ModifierKeys, (_, e) =>
        {
            e.Handled = true;
            action();
        });
    }

    private static void Remove()
    {
        HotkeyManager.Current.Remove("VantreLingo.Read");
        HotkeyManager.Current.Remove("VantreLingo.Review");
    }

    public void Dispose() => Remove();
}
