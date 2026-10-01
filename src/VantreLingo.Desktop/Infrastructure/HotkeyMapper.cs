// 从固定 STranslate Helpers/HotkeyMapper.cs 传统热键注册分支裁剪派生。
// Copyright © 2022 zggsong. MIT 许可证见 licenses/STranslate.MIT.txt。
// 移除 ChefKeys、低级键鼠钩子、按住键、Ctrl+CC、Ioc、正文日志及后台重试。
// 阅读/写作/复制翻译支持双路由：不带 Ctrl 走免费接口，同键另加 Ctrl 走模型。
using System.ComponentModel;
using System.IO;
using System.Windows.Input;
using NHotkey;
using NHotkey.Wpf;
using VantreLingo.Core.Configuration;

namespace VantreLingo.Desktop.Infrastructure;

internal sealed class HotkeyMapper(
    Action readFree, Action readModel, Action reviewFree, Action reviewModel,
    Action ocr, Action clipFree, Action clipModel, Action paste) : IDisposable
{
    private HotkeySettings _current = new() { ReadTranslate = "", WriteTranslate = "", ScreenshotOcr = "", ClipboardTranslate = "", PasteTranslation = "" };

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

    public void Suspend()
    {
        try { Remove(); }
        catch { }
    }

    public static void Validate(HotkeySettings settings)
    {
        var models = new List<HotkeyModel?>();
        foreach (var text in new[] { settings.ReadTranslate, settings.WriteTranslate, settings.ScreenshotOcr, settings.ClipboardTranslate, settings.PasteTranslation })
            models.Add(Parse(text));
        // 双路由展开：阅读/写作/复制翻译各可再带一个 +Ctrl 走模型版本。
        var effective = new List<HotkeyModel>();
        foreach (var text in new[] { settings.ReadTranslate, settings.WriteTranslate, settings.ClipboardTranslate })
        {
            var (free, model) = Split(text);
            if (free is { } f) effective.Add(f);
            if (model is { } m) effective.Add(m);
        }
        foreach (var parsed in models.OfType<HotkeyModel>())
            if (!effective.Contains(parsed)) effective.Add(parsed);
        if (effective.GroupBy(h => h).Any(g => g.Count() > 1))
            throw new InvalidDataException("热键不能相同（含自动派生的 +Ctrl 走模型版本）。");
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

    // 不带 Ctrl 的热键拆为免费版 +Ctrl 走模型版；本身已带 Ctrl 的只走模型。
    private static (HotkeyModel? Free, HotkeyModel? Model) Split(string text)
    {
        if (Parse(text) is not { } baseKey) return (null, null);
        if (baseKey.ModifierKeys.HasFlag(ModifierKeys.Control)) return (null, baseKey);
        return (baseKey, baseKey with { Ctrl = true });
    }

    private void Register(HotkeySettings settings)
    {
        var (readF, readM) = Split(settings.ReadTranslate);
        var (reviewF, reviewM) = Split(settings.WriteTranslate);
        var (clipF, clipM) = Split(settings.ClipboardTranslate);
        Add("VantreLingo.Read", readF, readFree);
        Add("VantreLingo.ReadModel", readM, readModel);
        Add("VantreLingo.Review", reviewF, reviewFree);
        Add("VantreLingo.ReviewModel", reviewM, reviewModel);
        Add("VantreLingo.Ocr", Parse(settings.ScreenshotOcr), ocr);
        Add("VantreLingo.Clipboard", clipF, clipFree);
        Add("VantreLingo.ClipboardModel", clipM, clipModel);
        Add("VantreLingo.Paste", Parse(settings.PasteTranslation), paste);
    }

    private static void Add(string id, HotkeyModel? hotkey, Action action)
    {
        if (hotkey is not { } key) return;
        HotkeyManager.Current.AddOrReplace(id, key.CharKey, key.ModifierKeys, (_, e) =>
        {
            e.Handled = true;
            try { action(); }
            catch (Exception ex) { App.LogCrash(ex); }
        });
    }

    private static void Remove()
    {
        foreach (var id in new[] { "VantreLingo.Read", "VantreLingo.ReadModel", "VantreLingo.Review",
            "VantreLingo.ReviewModel", "VantreLingo.Ocr", "VantreLingo.Clipboard", "VantreLingo.ClipboardModel", "VantreLingo.Paste" })
            HotkeyManager.Current.Remove(id);
    }

    public void Dispose() => Remove();
}
