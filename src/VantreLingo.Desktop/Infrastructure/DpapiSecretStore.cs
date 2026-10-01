using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace VantreLingo.Desktop.Infrastructure;

internal static class DpapiSecretStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("VantreLingo.Provider.v1");

    public static string Encrypt(string secret)
    {
        var bytes = Encoding.UTF8.GetBytes(secret);
        try
        {
            return Convert.ToBase64String(ProtectedData.Protect(bytes, Entropy, DataProtectionScope.CurrentUser));
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    public static string? Decrypt(string? encrypted)
    {
        if (string.IsNullOrEmpty(encrypted)) return null;
        byte[] bytes;
        try
        {
            bytes = ProtectedData.Unprotect(Convert.FromBase64String(encrypted), Entropy, DataProtectionScope.CurrentUser);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            throw new InvalidDataException("无法解密 API key，请使用原 Windows 用户或重新填写密钥。");
        }
        try { return Encoding.UTF8.GetString(bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
}
