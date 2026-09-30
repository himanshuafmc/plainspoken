namespace Plainspoken.Core.Settings;

public static class LanguagePresets
{
    /// <summary>BCP-47 codes sent as <c>language_codes</c>; empty means auto-detect (field omitted).</summary>
    public static IReadOnlyList<string> Codes(LanguagePreset preset) => preset switch
    {
        LanguagePreset.EnglishHindi => ["en-IN", "hi-IN"],
        LanguagePreset.English => ["en-IN"],
        _ => [],
    };

    public static string DisplayName(LanguagePreset preset) => preset switch
    {
        LanguagePreset.EnglishHindi => "English + Hindi (recommended)",
        LanguagePreset.English => "English only",
        _ => "Auto-detect",
    };
}
