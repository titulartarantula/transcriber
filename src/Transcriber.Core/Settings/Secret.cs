using System.Security.Cryptography;
using System.Text;

namespace Transcriber.Core.Settings;

/// <summary>Platform encryption for secrets at rest.</summary>
public interface ISecretProtector
{
    byte[] Protect(byte[] plain);

    byte[] Unprotect(byte[] cipher);
}

/// <summary>
/// Encrypts API keys and tokens so settings files never hold them in plain text. Windows uses DPAPI;
/// other platforms install their own <see cref="Protector"/> (Android Keystore) at startup.
/// </summary>
public static class Secret
{
    private static readonly byte[] Entropy = "Transcriber.v1"u8.ToArray();

    public static ISecretProtector? Protector { get; set; }

    public static string? Protect(string? plain)
    {
        if (string.IsNullOrEmpty(plain)) return null;
        return Convert.ToBase64String(ProtectBytes(Encoding.UTF8.GetBytes(plain)));
    }

    public static string Unprotect(string? cipher)
    {
        if (string.IsNullOrEmpty(cipher)) return "";
        try
        {
            return Encoding.UTF8.GetString(UnprotectBytes(Convert.FromBase64String(cipher)));
        }
        catch (Exception e) when (e is CryptographicException or FormatException)
        {
            // Settings copied from another user or device: treat the secret as unset.
            return "";
        }
    }

    private static byte[] ProtectBytes(byte[] plain)
    {
        if (Protector is not null) return Protector.Protect(plain);
        if (OperatingSystem.IsWindows()) return ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
        throw new PlatformNotSupportedException("No secret protector is configured for this platform.");
    }

    private static byte[] UnprotectBytes(byte[] cipher)
    {
        if (Protector is not null) return Protector.Unprotect(cipher);
        if (OperatingSystem.IsWindows()) return ProtectedData.Unprotect(cipher, Entropy, DataProtectionScope.CurrentUser);
        throw new PlatformNotSupportedException("No secret protector is configured for this platform.");
    }
}
