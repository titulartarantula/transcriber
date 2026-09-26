using System.Security.Cryptography;
using Android.Security.Keystore;
using Java.Security;
using Javax.Crypto;
using Javax.Crypto.Spec;
using CipherMode = Javax.Crypto.CipherMode;
using Transcriber.Core.Settings;

namespace Transcriber.Mobile;

/// <summary>AES-GCM with a key that lives in the Android Keystore and never leaves it.</summary>
public sealed class KeystoreProtector : ISecretProtector
{
    private const string Alias = "transcriber-secrets";
    private const string Transformation = "AES/GCM/NoPadding";

    public byte[] Protect(byte[] plain)
    {
        try
        {
            var cipher = Cipher.GetInstance(Transformation)!;
            cipher.Init(CipherMode.EncryptMode, Key());
            var iv = cipher.GetIV()!;
            var body = cipher.DoFinal(plain)!;
            return [(byte)iv.Length, .. iv, .. body];
        }
        catch (Java.Lang.Exception e)
        {
            throw new CryptographicException("Couldn't encrypt with the Android Keystore.", e);
        }
    }

    public byte[] Unprotect(byte[] data)
    {
        try
        {
            int ivLength = data[0];
            var cipher = Cipher.GetInstance(Transformation)!;
            cipher.Init(CipherMode.DecryptMode, Key(), new GCMParameterSpec(128, data[1..(1 + ivLength)]));
            return cipher.DoFinal(data[(1 + ivLength)..])!;
        }
        catch (Exception e) when (e is Java.Lang.Exception or IndexOutOfRangeException or ArgumentOutOfRangeException)
        {
            throw new CryptographicException("Couldn't decrypt with the Android Keystore.", e);
        }
    }

    private static IKey Key()
    {
        var store = KeyStore.GetInstance("AndroidKeyStore")!;
        store.Load(null);
        if (store.GetKey(Alias, null) is { } existing) return existing;

        var generator = KeyGenerator.GetInstance(KeyProperties.KeyAlgorithmAes, "AndroidKeyStore")!;
        generator.Init(new KeyGenParameterSpec.Builder(Alias, KeyStorePurpose.Encrypt | KeyStorePurpose.Decrypt)
            .SetBlockModes(KeyProperties.BlockModeGcm)!
            .SetEncryptionPaddings(KeyProperties.EncryptionPaddingNone)!
            .SetKeySize(256)!
            .Build());
        return generator.GenerateKey()!;
    }
}
