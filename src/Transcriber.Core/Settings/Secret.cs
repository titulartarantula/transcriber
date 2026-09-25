using System.Security.Cryptography;
using System.Text;

namespace Transcriber.Core.Settings;

/// <summary>Encrypts API keys with DPAPI so settings.json never holds them in plain text.</summary>
internal static class Secret
{
    private static readonly byte[] Entropy = "Transcriber.v1"u8.ToArray();

    public static string? Protect(string? plain)
    {
        if (string.IsNullOrEmpty(plain)) return null;
        var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(bytes);
    }

    public static string Unprotect(string? cipher)
    {
        if (string.IsNullOrEmpty(cipher)) return "";
        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(cipher), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (Exception e) when (e is CryptographicException or FormatException)
        {
            // Settings copied from another user or machine: treat the key as unset.
            return "";
        }
    }
}
