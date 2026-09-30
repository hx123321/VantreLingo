using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using VantreLingo.Core;
using VantreLingo.Core.Configuration;
using VantreLingo.Desktop.Infrastructure;

namespace VantreLingo.Desktop.Views;

public partial class ToolWindow
{
    private InquiryReport? _inquiry;
    private List<FieldRow> _customerRows = [];
    private readonly Dictionary<string, List<FieldRow>> _productRows = [];
    private static readonly string[] FieldStatuses = ["known", "missing", "ambiguous", "conflicting", "not_applicable"];
    internal void PrepareInquiry()
    {
        ConfigurationChanged();
        SetStatus("请在原文框明确输入或粘贴客户文本，再点击客户整理。不会读取其他消息或附件。");
    }
    private void TranslationView_Click(object sender, RoutedEventArgs e) => TranslationView();
    private void TranslationView()
    {
        TranslationPanel.Visibility = Visibility.Visible; InquiryPanel.Visibility = Visibility.Collapsed;
    }
    private void ResetInquiry()
    {
        _inquiry = null; _customerRows = []; _productRows.Clear();
        InquiryMarkdownButton.IsEnabled = InquiryJsonButton.IsEnabled = SaveInquiryButton.IsEnabled = false;
        CustomerGrid.ItemsSource = null; ProductGrid.ItemsSource = null; ProductList.ItemsSource = null;
        InquirySource.Clear(); InquiryIssues.Clear(); InquiryQuestions.Clear();
    }
    private async void Inquiry_Click(object sender, RoutedEventArgs e)
    {
        var source = InputText.Text;
        var operation = BeginOperation();
        _snapshot = null; _readOnly = true;
        SetStatus("正在整理客户信息…仅发送当前明确提供的文本。");
        try
        {
            var provider = _app.Providers.Selected;
            var client = new OpenAiCompatibleClient(_http, provider, DpapiSecretStore.Decrypt(provider.EncryptedApiKey));
            var json = await client.CompleteJsonAsync(InquiryService.SystemPrompt, source, operation.Token);
            var report = InquiryService.Validate(source, InquiryService.Parse(json));
            _operations.TryPublish(operation, () =>
            {
                _inquiry = report; _customerRows = Rows(report.Customer);
                foreach (var product in report.Products) _productRows[product.Id] = Rows(product.Fields);
                CustomerStatusColumn.ItemsSource = ProductStatusColumn.ItemsSource = FieldStatuses;
                CustomerGrid.ItemsSource = _customerRows;
                ProductList.ItemsSource = report.Products; ProductList.SelectedIndex = 0;
                InquirySource.Text = source;
                InquiryIssues.Text = string.Join("\n\n", report.Issues.Select(i => $"{i.Path} · {i.Status} · {i.Priority}\n{i.Reason}"));
                InquiryQuestions.Text = string.Join("\n\n", report.Questions);
                TranslationPanel.Visibility = Visibility.Collapsed; InquiryPanel.Visibility = Visibility.Visible;
                InquiryMarkdownButton.IsEnabled = InquiryJsonButton.IsEnabled = SaveInquiryButton.IsEnabled = true;
                IntentLabel.Text = "客户整理 · 原文证据与人工审查";
                SetStatus("整理完成。字段可人工修改，导出会标注人工修改；正文默认只留在内存。建议追问不会自动发送。");
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _operations.TryPublish(operation, () => SetStatus(ex is InvalidDataException ? ex.Message : "客户整理失败，请检查 Provider 后重试。原文保持不变。"));
        }
        finally { _operations.TryPublish(operation, () => CancelButton.IsEnabled = _inquiry is not null); }
    }
    private static List<FieldRow> Rows(Dictionary<string, InquiryField> fields) => fields.Select(kv => new FieldRow(kv.Key, kv.Value)).ToList();
    private void Product_Changed(object sender, SelectionChangedEventArgs e)
    { ProductGrid.ItemsSource = ProductList.SelectedItem is InquiryProduct p ? _productRows.GetValueOrDefault(p.Id) : null; }
    private InquiryReport EditedReport()
    {
        CustomerGrid.CommitEdit(DataGridEditingUnit.Cell, true); CustomerGrid.CommitEdit(DataGridEditingUnit.Row, true);
        ProductGrid.CommitEdit(DataGridEditingUnit.Cell, true); ProductGrid.CommitEdit(DataGridEditingUnit.Row, true);
        if (_inquiry is null) throw new InvalidDataException("没有有效整理结果。");
        static Dictionary<string, InquiryField> Fields(List<FieldRow> rows) => rows.ToDictionary(r => r.Name, r => r.ToField());
        // 人工纠正明确标注，不将用户修改的值伪装为模型已验证事实。
        var customer = Fields(_customerRows);
        var products = _inquiry.Products.Select(p => p with { Fields = Fields(_productRows[p.Id]) }).ToList();
        var issues = customer.Select(kv => (Path: "customer." + kv.Key, Field: kv.Value))
            .Concat(products.SelectMany(p => p.Fields.Select(kv => (Path: p.Id + "." + kv.Key, Field: kv.Value))))
            .Where(x => x.Field.Status is "ambiguous" or "conflicting" || (x.Field.Status == "missing" && x.Field.Priority != "optional"))
            .Select(x => new InquiryIssue(x.Path, x.Field.Status, x.Field.Priority, x.Field.Reason)).ToList();
        return _inquiry with { Customer = customer, Products = products, Issues = issues, Questions = issues.Select(i => $"请确认 {i.Path}：{i.Reason}").ToList() };
    }
    private void InquiryMarkdown_Click(object sender, RoutedEventArgs e) => ExportInquiry(copy: true, json: false);
    private void InquiryJson_Click(object sender, RoutedEventArgs e) => ExportInquiry(copy: true, json: true);
    private void SaveInquiry_Click(object sender, RoutedEventArgs e) => ExportInquiry(copy: false, json: false);
    private void ExportInquiry(bool copy, bool json)
    {
        if (_operation is null || _inquiry is null) return;
        var operation = _operation;
        string? path = null;
        if (!copy)
        {
            var dialog = new SaveFileDialog { Filter = "Markdown|*.md|JSON（包含原文）|*.json", FileName = "inquiry.md" };
            if (dialog.ShowDialog(this) != true) return;
            path = dialog.FileName; json = dialog.FilterIndex == 2;
        }
        // 模态文件选择期间可能开始新的请求，必须在选择完后重新检查所有权。
        _operations.TryPublish(operation, () =>
        {
            try
            {
                var report = EditedReport();
                var text = json ? JsonSerializer.Serialize(report, JsonFormat.Options) : InquiryService.Markdown(report);
                if (copy) { Clipboard.SetText(text); SetStatus("当前人工审查结果已复制。"); }
                else { File.WriteAllText(path!, text); SetStatus("已按你的选择另存当前整理结果。"); }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException or InvalidDataException)
            { SetStatus(ex is InvalidDataException ? ex.Message : "导出失败，请检查文件权限或剪贴板占用。"); }
        });
    }
    private sealed class FieldRow(string name, InquiryField original)
    {
        public string Name { get; } = name;
        public string Status { get; set; } = original.Status;
        public string? Value { get; set; } = original.Value;
        public string? Evidence => original.Evidence;
        public string Reason => original.Reason;
        public InquiryField ToField()
        {
            if (!FieldStatuses.Contains(Status) || (Value?.Length ?? 0) > 2000) throw new InvalidDataException("人工字段状态或长度无效。");
            return original with { Status = Status, Value = Status is "missing" or "not_applicable" ? null : Value,
                Reason = Status != original.Status || Value != original.Value ? "人工修改，原证据供参考；请自行确认事实。" : original.Reason };
        }
    }
}
