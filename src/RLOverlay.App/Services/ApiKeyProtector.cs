using System.Security.Cryptography;
using System.Text;

namespace RLOverlay.App.Services;

/// <summary>Encrypts the tracker.gg API key with Windows DPAPI (current user) before it goes into settings.json.</summary>
internal static class ApiKeyProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("RLOverlay.TrackerGgApiKey.v1");

    public static string? Protect(string? apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return null;
        }

        var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(apiKey.Trim()), Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(bytes);
    }

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
            // Settings copied from another user/PC: the key has to be entered again.
            return null;
        }
    }
}
