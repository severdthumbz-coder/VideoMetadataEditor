using System.Security.Cryptography;
using System.Text;

namespace VideoMetadataEditor.Services;

/// <summary>
/// Wraps Windows DPAPI (Data Protection API) for storing API keys.
/// Keys are encrypted with the current user's credentials — only the same
/// Windows user account on the same machine can decrypt them.
///
/// Storage format in config.json:  "DPAPI:Base64EncodedCiphertext"
/// Plain-text keys (from old config files) are transparently upgraded on first read.
/// </summary>
public static class SecureKeyService
{
    private const string Prefix = "DPAPI:";

    /// <summary>
    /// Returns the decrypted key. If the stored value is plain text (no prefix),
    /// it is treated as-is and will be encrypted next time SaveAsync() runs.
    /// Returns empty string if decryption fails.
    /// </summary>
    public static string Decrypt(string stored)
    {
        if (string.IsNullOrWhiteSpace(stored)) return string.Empty;
        if (!stored.StartsWith(Prefix, StringComparison.Ordinal)) return stored; // plain text — legacy

        try
        {
            var cipherBytes  = Convert.FromBase64String(stored[Prefix.Length..]);
            var plainBytes   = ProtectedData.Unprotect(cipherBytes, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plainBytes);
        }
        catch { return string.Empty; }
    }

    /// <summary>
    /// Returns the DPAPI-encrypted form of <paramref name="plainText"/>.
    /// Always returns a "DPAPI:" prefixed Base64 string.
    /// If already encrypted (has prefix), returns as-is.
    /// </summary>
    public static string Encrypt(string plainText)
    {
        if (string.IsNullOrWhiteSpace(plainText)) return string.Empty;
        if (plainText.StartsWith(Prefix, StringComparison.Ordinal)) return plainText; // already encrypted

        try
        {
            var plainBytes   = Encoding.UTF8.GetBytes(plainText);
            var cipherBytes  = ProtectedData.Protect(plainBytes, null, DataProtectionScope.CurrentUser);
            return Prefix + Convert.ToBase64String(cipherBytes);
        }
        catch { return plainText; } // fallback: store plain if DPAPI unavailable (e.g. Wine)
    }

    /// <summary>True if the stored value is a DPAPI-encrypted blob.</summary>
    public static bool IsEncrypted(string stored) =>
        !string.IsNullOrEmpty(stored) && stored.StartsWith(Prefix, StringComparison.Ordinal);
}
