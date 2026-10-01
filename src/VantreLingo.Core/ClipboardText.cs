using System.Security.Cryptography;
using System.Text;

namespace VantreLingo.Core;

// 纯文本清洗：永不抛异常之外的受控 InvalidData finally 由调用方捕获，
// 这里只做可单元测试的截断与有效性判断，WPF 剪贴板异常由 Desktop 层吞掉。
public static class ClipboardText
{
    public const int MaxLength = 30_000;

    public static string? Sanitize(string? text)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            var trimmed = text.Trim();
            if (trimmed.Length == 0 || trimmed.Length > MaxLength || trimmed.Contains('\0')) return null;
            return trimmed;
        }
        catch
        {
            return null;
        }
    }

    public static string Hash(string text)
    {
        try { return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))); }
        catch { return string.Empty; }
    }
}
