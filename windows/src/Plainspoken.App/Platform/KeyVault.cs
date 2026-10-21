using Plainspoken.Core.Settings;

namespace Plainspoken.App.Platform;

/// <summary>
/// The saved keys, one per key slot (see <see cref="SpeechServices.KeySlot"/>), each encrypted with DPAPI.
/// The Gemini key keeps its original place (<see cref="LocalSettings.ApiKeyProtected"/>) so older files still work.
/// </summary>
internal static class KeyVault
{
    public static string? Get(PlainspokenSettings settings, string slot)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (slot == SpeechServices.GeminiKey)
        {
            return ApiKeyProtector.Unprotect(settings.Local.ApiKeyProtected);
        }

        return settings.Local.ApiKeysProtected.TryGetValue(slot, out var p) ? ApiKeyProtector.Unprotect(p) : null;
    }

    /// <summary>Saves (or, for an empty key, removes) the key of <paramref name="slot"/>.</summary>
    public static void Set(PlainspokenSettings settings, string slot, string? key)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var protectedKey = ApiKeyProtector.Protect(key);
        if (slot == SpeechServices.GeminiKey)
        {
            settings.Local.ApiKeyProtected = protectedKey;
        }
        else if (protectedKey is null)
        {
            settings.Local.ApiKeysProtected.Remove(slot);
        }
        else
        {
            settings.Local.ApiKeysProtected[slot] = protectedKey;
        }
    }

    public static bool Has(PlainspokenSettings settings, string slot) => !string.IsNullOrEmpty(Get(settings, slot));
}
