using System.Security.Cryptography;
using System.Text;

namespace VantreLingo.Core;

public sealed record SelectionSnapshot(
    long OperationId,
    int ProcessId,
    nint TopLevelWindow,
    string? AutomationId,
    string? ControlType,
    string OriginalText,
    string OriginalHash,
    DateTimeOffset CapturedAt,
    SelectionLocator? Locator = null)
{
    public static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}

// 只有能重新读取同一控件、范围和上下文的入口才能提供 Locator。
public sealed record SelectionLocator(nint Control, int Start, int End, string ControlTextHash);
