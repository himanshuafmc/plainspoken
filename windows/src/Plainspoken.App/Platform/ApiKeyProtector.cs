using System.Security.Cryptography;
using System.Text;

namespace Plainspoken.App.Platform;

/// <summary>Encrypts the API key with DPAPI (CurrentUser scope). Only this Windows user can decrypt it.</summary>
internal static class ApiKeyProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Plainspoken.ApiKey.v1");

    public static string? Protect(string? apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return null;
        }

        var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(apiKey.Trim()), Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(bytes);
    }

    /// <summary>Returns null if missing or unreadable (e.g. settings copied from another PC/user).</summary>
    public static string? Unprotect(string? protectedKey)
    {
        if (string.IsNullOrWhiteSpace(protectedKey))
        {
            return null;
        }

        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(protectedKey), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return null;
        }
    }
}
