using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using VantreLingo.Core;
using VantreLingo.Core.Configuration;

namespace VantreLingo.Desktop.Views;

public partial class LibraryEditor : UserControl
{
    private readonly App _app;
    private string _presetId = "", _termId = "";
    private bool _loading;
    public LibraryEditor() : this((App)Application.Current) { }
    internal LibraryEditor(App app)
    {
        _app = app; InitializeComponent();
        Bind(Scene, StyleChoices.Scenes); Bind(Domain, StyleChoices.Domains); Bind(Tone, StyleChoices.Tones);
        Bind(Persona, StyleChoices.Personas); Bind(Length, StyleChoices.Lengths); Bind(Fidelity, StyleChoices.Fidelities);
        Bind(TermStatus, new Dictionary<string, string> { ["draft"] = "草稿", ["active"] = "启用", ["disabled"] = "禁用" });
        Bind(TermDirection, new Dictionary<string, string> { ["forward"] = "正向", ["reverse"] = "反向", ["both"] = "双向" });
        Refresh();
    }
    private static void Bind(ComboBox box, IReadOnlyDictionary<string, string> choices)
    { box.ItemsSource = choices; box.DisplayMemberPath = "Value"; box.SelectedValuePath = "Key"; }
    private void Refresh()
    {
        PresetList.ItemsSource = _app.Presets.Presets;
        PresetList.SelectedItem = _app.Presets.Presets.FirstOrDefault(p => p.Id == _presetId) ?? _app.Presets.Presets[0];
        TermList.ItemsSource = _app.Glossary.Terms;
        TermList.SelectedItem = _app.Glossary.Terms.FirstOrDefault(t => t.Id == _termId);
        if (TermList.SelectedItem is null) LoadTerm(new());
    }
    private void Preset_Changed(object sender, SelectionChangedEventArgs e)
    { if (PresetList.SelectedItem is StylePreset p) LoadPreset(p); }
    private void LoadPreset(StylePreset p)
    {
        _loading = true;
        _presetId = p.Id; PresetName.Text = p.Name; Scene.SelectedValue = p.Scene; Domain.SelectedValue = p.Domain;
        CustomDomain.Text = p.CustomDomain ?? "";
        Tone.SelectedValue = p.Tone; Persona.SelectedValue = p.Persona; Length.SelectedValue = p.Length;
        Fidelity.SelectedValue = p.Fidelity; Fidelity.IsEnabled = p.Scene != "formal_document"; CustomPrompt.Text = p.CustomPrompt ?? "";
        _loading = false;
    }
    private StylePreset ReadPreset()
    {
        var original = _app.Presets.Presets.FirstOrDefault(p => p.Id == _presetId) ?? new() { Id = _presetId };
        var p = (original with { Name = PresetName.Text.Trim(), Scene = Value(Scene), Domain = Value(Domain), Tone = Value(Tone),
            Persona = Value(Persona), CustomDomain = Value(Domain) == "custom" ? CustomDomain.Text.Trim() : null, Length = Value(Length), Fidelity = Value(Fidelity), CustomPrompt = string.IsNullOrWhiteSpace(CustomPrompt.Text) ? null : CustomPrompt.Text }).Normalized();
        if (p.Fingerprint() != original.Fingerprint()) p = p with { QuickModeApproved = false, TestedPromptHash = null, ApprovedPromptHash = null };
        p.Validate(); return p;
    }
    private static string Value(ComboBox box) => box.SelectedValue as string ?? "";
    private void Scene_Changed(object sender, SelectionChangedEventArgs e)
    { if (_loading || Fidelity is null) return; Fidelity.IsEnabled = Value(Scene) != "formal_document"; if (!Fidelity.IsEnabled) Fidelity.SelectedValue = "strict"; }
    private void NewPreset_Click(object sender, RoutedEventArgs e) { PresetList.SelectedIndex = -1; LoadPreset(new() { Id = Guid.NewGuid().ToString("N") }); }
    private void SavePreset_Click(object sender, RoutedEventArgs e) => Run(() =>
    {
        var p = ReadPreset(); SavePreset(p); StatusText.Text = "风格已保存。请在工具窗口选择并测试。";
    });
    private void SavePreset(StylePreset p)
    {
        _app.SavePresets(_app.Presets with { Presets = _app.Presets.Presets.Where(x => x.Id != p.Id).Append(p).ToList() });
        _presetId = p.Id; Refresh();
    }
    private void DeletePreset_Click(object sender, RoutedEventArgs e) => Run(() =>
    { _app.SavePresets(_app.Presets with { Presets = _app.Presets.Presets.Where(p => p.Id != _presetId).ToList() }); Refresh(); });
    private void Approve_Click(object sender, RoutedEventArgs e) => Run(() =>
    { SavePreset(ReadPreset().ApproveFast()); StatusText.Text = "已明确授权当前已测试配置用于快速模式。"; });
    private void Term_Changed(object sender, SelectionChangedEventArgs e)
    { if (TermList.SelectedItem is GlossaryTerm t) LoadTerm(t); }
    private void LoadTerm(GlossaryTerm t)
    {
        _termId = t.Id; TermStatus.SelectedValue = t.Status; TermSourceLanguage.Text = t.SourceLanguage;
        TermTargetLanguage.Text = t.TargetLanguage; TermDirection.SelectedValue = t.Direction; TermSource.Text = t.Source;
        TermTarget.Text = t.Target; TermContexts.Text = string.Join(", ", t.Contexts); AllowedVariants.Text = string.Join("\n", t.AllowedVariants);
        ForbiddenVariants.Text = string.Join("\n", t.ForbiddenVariants); TermNotes.Text = t.Notes;
    }
    private void NewTerm_Click(object sender, RoutedEventArgs e) { TermList.SelectedIndex = -1; LoadTerm(new()); }
    private void SaveTerm_Click(object sender, RoutedEventArgs e) => Run(() =>
    {
        var t = new GlossaryTerm { Id = _termId, Status = Value(TermStatus), SourceLanguage = TermSourceLanguage.Text.Trim(),
            TargetLanguage = TermTargetLanguage.Text.Trim(), Direction = Value(TermDirection), Source = TermSource.Text.Trim(), Target = TermTarget.Text.Trim(),
            Contexts = Split(TermContexts.Text, ','), AllowedVariants = Split(AllowedVariants.Text, '\n'), ForbiddenVariants = Split(ForbiddenVariants.Text, '\n'), Notes = TermNotes.Text };
        _app.SaveGlossary(_app.Glossary with { Terms = _app.Glossary.Terms.Where(x => x.Id != t.Id).Append(t).ToList() });
        Refresh(); StatusText.Text = "词条已保存。";
    });
    private static string[] Split(string text, char separator) => text.Split(separator, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Distinct().ToArray();
    private void DeleteTerm_Click(object sender, RoutedEventArgs e) => Run(() =>
    { _app.SaveGlossary(_app.Glossary with { Terms = _app.Glossary.Terms.Where(t => t.Id != _termId).ToList() }); Refresh(); });
    private void ImportPresets_Click(object sender, RoutedEventArgs e) => Run(() =>
    { var p = Import<PresetCatalog>(); if (p is null) return; p.Validate(); _app.SavePresets(p.Imported()); Refresh(); });
    private void ExportPresets_Click(object sender, RoutedEventArgs e) => Run(() => Export(_app.Presets.Imported(), "presets.json"));
    private void ImportTerms_Click(object sender, RoutedEventArgs e) => Run(() =>
    { var g = Import<GlossaryCatalog>(); if (g is null) return; g.Validate(); _app.SaveGlossary(g); Refresh(); });
    private void ExportTerms_Click(object sender, RoutedEventArgs e) => Run(() => Export(_app.Glossary, "glossary.json"));
    private T? Import<T>() where T : class
    {
        var dialog = new OpenFileDialog { Filter = "JSON|*.json" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return null;
        if (new FileInfo(dialog.FileName).Length > 4_000_000) throw new InvalidDataException("导入文件过大。");
        return JsonSerializer.Deserialize<T>(File.ReadAllText(dialog.FileName), JsonFormat.Options) ?? throw new InvalidDataException("文件不能为空。");
    }
    private void Export<T>(T value, string name)
    {
        var dialog = new SaveFileDialog { Filter = "JSON|*.json", FileName = name };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) File.WriteAllText(dialog.FileName, JsonSerializer.Serialize(value, JsonFormat.Options));
    }
    private void Run(Action action)
    {
        try { action(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        { StatusText.Text = ex is InvalidDataException ? ex.Message : "文件读取、结构校验或保存失败；当前已保存配置保持不变。"; }
    }
}
