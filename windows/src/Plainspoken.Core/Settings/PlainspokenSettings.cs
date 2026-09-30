namespace Plainspoken.Core.Settings;

public enum EngineKind
{
    /// <summary>gemini-3.5-transcribe via the Interactions API (default).</summary>
    Transcribe,

    /// <summary>Flash-Lite via generateContent with a clean-up prompt (fallback).</summary>
    Generate,
}

public enum LanguagePreset
{
    EnglishHindi,
    Auto,
    English,
}

public enum TranscriptionMode
{
    Smart,
    Verbatim,
}

public enum InsertionMethod
{
    Paste,
    Type,
}

/// <summary>
/// All user settings. Mirrors docs/SPEC.md §6 and shared/settings.schema.json.
/// Everything except <see cref="Local"/> can be exported and shared (and imported on Android).
/// </summary>
public sealed class PlainspokenSettings
{
    public const int CurrentSchemaVersion = 1;
    public const string DefaultHotkey = "Ctrl+Alt+Space";

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public TranscriptionSettings Transcription { get; set; } = new();

    public RecordingSettings Recording { get; set; } = new();

    public InsertionSettings Insertion { get; set; } = new();

    public HistorySettings History { get; set; } = new();

    public string Hotkey { get; set; } = DefaultHotkey;

    /// <summary>Show a small dictation button on screen while idle (Windows).</summary>
    public bool ShowMiniButton { get; set; } = true;

    /// <summary>This device only. Never exported.</summary>
    public LocalSettings Local { get; set; } = new();

    /// <summary>Fills missing sections, clamps numbers and cleans the vocabulary. Returns this.</summary>
    public PlainspokenSettings Normalize()
    {
        SchemaVersion = CurrentSchemaVersion;
        Transcription ??= new();
        Recording ??= new();
        Insertion ??= new();
        History ??= new();
        Local ??= new();

        var t = Transcription;
        t.TranscribeModel = string.IsNullOrWhiteSpace(t.TranscribeModel) ? TranscriptionSettings.DefaultTranscribeModel : t.TranscribeModel.Trim();
        t.GenerateModel = string.IsNullOrWhiteSpace(t.GenerateModel) ? TranscriptionSettings.DefaultGenerateModel : t.GenerateModel.Trim();
        t.LiveModel = string.IsNullOrWhiteSpace(t.LiveModel) ? TranscriptionSettings.DefaultLiveModel : t.LiveModel.Trim();
        t.ApiBaseUrl = NormalizeBaseUrl(t.ApiBaseUrl);
        t.RequestTimeoutSeconds = Math.Clamp(t.RequestTimeoutSeconds, 10, 600);
        t.CustomVocabulary = VocabularyCleaner.Clean(t.CustomVocabulary ?? []);

        Recording.MaxSeconds = Math.Clamp(Recording.MaxSeconds, 30, 1800);

        Hotkey = HotkeyGesture.TryParse(Hotkey, out var g, out _) ? g.ToString() : DefaultHotkey;
        return this;
    }

    public static string NormalizeBaseUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) ||
            !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            return TranscriptionSettings.DefaultApiBaseUrl;
        }

        return uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
    }
}

public sealed class TranscriptionSettings
{
    public const string DefaultTranscribeModel = "gemini-3.5-transcribe";
    public const string DefaultGenerateModel = "gemini-3.5-flash-lite";
    public const string DefaultApiBaseUrl = "https://generativelanguage.googleapis.com";
    public const string DefaultLiveModel = "gemini-3.5-transcribe-live";

    public EngineKind Engine { get; set; } = EngineKind.Transcribe;

    public string TranscribeModel { get; set; } = DefaultTranscribeModel;

    public string GenerateModel { get; set; } = DefaultGenerateModel;

    public string ApiBaseUrl { get; set; } = DefaultApiBaseUrl;

    public int RequestTimeoutSeconds { get; set; } = 60;

    public LanguagePreset Languages { get; set; } = LanguagePreset.EnglishHindi;

    public TranscriptionMode Mode { get; set; } = TranscriptionMode.Smart;

    /// <summary>On a 429 from the selected engine, try the other engine once.</summary>
    public bool UseBackupEngineWhenLimited { get; set; } = true;

    public List<string> CustomVocabulary { get; set; } = [];

    /// <summary>Stream audio to the live model while the user speaks (on by default); falls back to the normal engine.</summary>
    public bool LiveStreaming { get; set; } = true;

    public string LiveModel { get; set; } = DefaultLiveModel;

    public string ModelFor(EngineKind engine) => engine == EngineKind.Transcribe ? TranscribeModel : GenerateModel;
}

public sealed class RecordingSettings
{
    public int MaxSeconds { get; set; } = 600;

    public bool Sounds { get; set; } = true;
}

public sealed class InsertionSettings
{
    public InsertionMethod Method { get; set; } = InsertionMethod.Paste;

    public bool TrailingSpace { get; set; } = true;
}

public sealed class HistorySettings
{
    public bool Enabled { get; set; } = true;
}

public sealed class LocalSettings
{
    /// <summary>API key encrypted by the platform (Windows: DPAPI CurrentUser), base64. Never plain text.</summary>
    public string? ApiKeyProtected { get; set; }

    /// <summary>Capture device id; null = system default.</summary>
    public string? MicrophoneId { get; set; }

    public bool StartWithWindows { get; set; }

    public bool FirstRunCompleted { get; set; }

    /// <summary>Where the user dragged the mini button: "x,y" in screen pixels (its bottom-centre); null = default.</summary>
    public string? MiniButtonPosition { get; set; }
}
