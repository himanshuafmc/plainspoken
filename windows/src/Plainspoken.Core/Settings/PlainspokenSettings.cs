using System.Text.Json.Serialization;

namespace Plainspoken.Core.Settings;

/// <summary>The two Gemini models. Kept in step with <see cref="TranscriptionSettings.ServiceOrder"/> for older readers.</summary>
public enum EngineKind
{
    /// <summary>gemini-3.5-transcribe via the Interactions API.</summary>
    Transcribe,

    /// <summary>A general Gemini model via generateContent with a transcription prompt.</summary>
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

/// <summary>How Hindi words are written in the final text.</summary>
public enum HindiScript
{
    /// <summary>Roman letters, the way people type Hinglish ("kal meeting hai").</summary>
    Roman,

    Devanagari,

    /// <summary>Whatever the speech service returns.</summary>
    AsSpoken,
}

/// <summary>Who tidies up raw transcripts in Smart mode.</summary>
public enum CleanupProvider
{
    Gemini,

    /// <summary>Any service with an OpenAI-style /chat/completions endpoint.</summary>
    [JsonStringEnumMemberName("openai")]
    OpenAI,

    Off,
}

/// <summary>Windows: the key that records while it is held down.</summary>
public enum HoldKey
{
    Off,
    RightCtrl,
    RightAlt,
    RightShift,
}

/// <summary>
/// All user settings. Mirrors docs/SPEC.md §6 and shared/settings.schema.json.
/// Everything except <see cref="Local"/> can be exported and shared (and imported on Android).
/// </summary>
public sealed class PlainspokenSettings
{
    public const int CurrentSchemaVersion = 2;
    public const string DefaultHotkey = "Ctrl+Alt+Space";

    /// <summary>The general model's default before 0.4.0; the more accurate gemini-3.5-flash replaces it.</summary>
    private const string OldGenerateModel = "gemini-3.5-flash-lite";

    /// <summary>Files from before 0.4.0 (Android) / 0.5.0 (Windows) say 1 and are migrated by <see cref="Normalize"/>.</summary>
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public TranscriptionSettings Transcription { get; set; } = new();

    public RecordingSettings Recording { get; set; } = new();

    public InsertionSettings Insertion { get; set; } = new();

    public HistorySettings History { get; set; } = new();

    public string Hotkey { get; set; } = DefaultHotkey;

    /// <summary>Windows: record while this key is held down (the hotkey still toggles).</summary>
    public HoldKey HoldKey { get; set; } = HoldKey.RightCtrl;

    /// <summary>Show a small dictation button on screen while idle (Windows).</summary>
    public bool ShowMiniButton { get; set; } = true;

    /// <summary>Spoken shortcuts: saying <see cref="Snippet.Trigger"/> types <see cref="Snippet.Text"/>.</summary>
    public List<Snippet> Snippets { get; set; } = [];

    /// <summary>This device only. Never exported.</summary>
    public LocalSettings Local { get; set; } = new();

    /// <summary>Fills missing sections, migrates older files, clamps numbers and cleans lists. Returns this.</summary>
    public PlainspokenSettings Normalize()
    {
        Transcription ??= new();
        Recording ??= new();
        Insertion ??= new();
        History ??= new();
        Local ??= new();
        Local.ApiKeysProtected ??= new(StringComparer.Ordinal);

        var t = Transcription;
        if (SchemaVersion < 2)
        {
            MigrateFromVersion1(t);
        }

        SchemaVersion = CurrentSchemaVersion;
        t.TranscribeModel = string.IsNullOrWhiteSpace(t.TranscribeModel) ? TranscriptionSettings.DefaultTranscribeModel : t.TranscribeModel.Trim();
        t.GenerateModel = string.IsNullOrWhiteSpace(t.GenerateModel) ? TranscriptionSettings.DefaultGenerateModel : t.GenerateModel.Trim();
        t.LiveModel = string.IsNullOrWhiteSpace(t.LiveModel) ? TranscriptionSettings.DefaultLiveModel : t.LiveModel.Trim();
        t.ApiBaseUrl = NormalizeBaseUrl(t.ApiBaseUrl);
        t.RequestTimeoutSeconds = Math.Clamp(t.RequestTimeoutSeconds, 10, 600);
        t.CustomVocabulary = VocabularyCleaner.Clean(t.CustomVocabulary ?? []);
        t.ServiceOrder = SpeechServices.CleanOrder(t.ServiceOrder);

        // Keep the version 1 fields in step, so an older Plainspoken reading this file behaves sensibly.
        t.Engine = t.ServiceOrder.FirstOrDefault(id => id.StartsWith("gemini", StringComparison.Ordinal)) == SpeechService.GeminiGeneral.Id()
            ? EngineKind.Generate
            : EngineKind.Transcribe;
        t.UseBackupEngineWhenLimited = t.ServiceOrder.Contains(SpeechService.Gemini.Id()) && t.ServiceOrder.Contains(SpeechService.GeminiGeneral.Id());

        t.Services ??= new();
        t.Services.Normalize();
        t.Cleanup ??= new();
        t.Cleanup.Normalize();
        t.HedgeSeconds = Math.Clamp(t.HedgeSeconds, 0, 30);
        var currency = t.Currency?.Trim() ?? string.Empty;
        t.Currency = currency.Length == 0 ? "₹" : currency[..Math.Min(4, currency.Length)];
        Snippets = SnippetCleaner.Clean(Snippets);

        Recording.MaxSeconds = Math.Clamp(Recording.MaxSeconds, 30, 1800);

        Hotkey = HotkeyGesture.TryParse(Hotkey, out var g, out _) ? g.ToString() : DefaultHotkey;
        return this;
    }

    /// <summary>Version 1 had two Gemini engines and a "use the backup when limited" switch; they become the service order.</summary>
    private static void MigrateFromVersion1(TranscriptionSettings t)
    {
        var primary = t.Engine == EngineKind.Generate ? SpeechService.GeminiGeneral : SpeechService.Gemini;
        var other = primary == SpeechService.Gemini ? SpeechService.GeminiGeneral : SpeechService.Gemini;
        t.ServiceOrder = t.UseBackupEngineWhenLimited ? [primary.Id(), other.Id()] : [primary.Id()];
        if (string.Equals(t.GenerateModel?.Trim(), OldGenerateModel, StringComparison.Ordinal))
        {
            t.GenerateModel = TranscriptionSettings.DefaultGenerateModel;
        }
    }

    /// <summary>An http(s) address without a trailing slash, or <paramref name="fallback"/> when it isn't one. Paths (like /v1) are kept.</summary>
    public static string NormalizeBaseUrl(string? url, string fallback = TranscriptionSettings.DefaultApiBaseUrl)
    {
        if (string.IsNullOrWhiteSpace(url) ||
            !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) ||
            uri.Host.Length == 0)
        {
            return fallback;
        }

        return uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
    }
}

public sealed class TranscriptionSettings
{
    public const string DefaultTranscribeModel = "gemini-3.5-transcribe";
    public const string DefaultGenerateModel = "gemini-3.5-flash";
    public const string DefaultApiBaseUrl = "https://generativelanguage.googleapis.com";
    public const string DefaultLiveModel = "gemini-3.5-transcribe-live";

    /// <summary>Version 1 field, kept in step with <see cref="ServiceOrder"/>.</summary>
    public EngineKind Engine { get; set; } = EngineKind.Transcribe;

    public string TranscribeModel { get; set; } = DefaultTranscribeModel;

    public string GenerateModel { get; set; } = DefaultGenerateModel;

    /// <summary>The Gemini API address (both Gemini services and the Gemini clean-up use it).</summary>
    public string ApiBaseUrl { get; set; } = DefaultApiBaseUrl;

    public int RequestTimeoutSeconds { get; set; } = 60;

    public LanguagePreset Languages { get; set; } = LanguagePreset.EnglishHindi;

    public TranscriptionMode Mode { get; set; } = TranscriptionMode.Smart;

    /// <summary>Version 1 field, kept in step with <see cref="ServiceOrder"/>.</summary>
    public bool UseBackupEngineWhenLimited { get; set; } = true;

    public List<string> CustomVocabulary { get; set; } = [];

    /// <summary>Stream audio to the Gemini live model while the user speaks; falls back to the normal request.</summary>
    public bool LiveStreaming { get; set; } = true;

    public string LiveModel { get; set; } = DefaultLiveModel;

    /// <summary>Speech services to use, main first, then up to two backups (ids of <see cref="SpeechService"/>).</summary>
    public List<string> ServiceOrder { get; set; } = [SpeechService.Gemini.Id(), SpeechService.GeminiGeneral.Id()];

    public ServiceSettingsMap Services { get; set; } = new();

    public CleanupSettings Cleanup { get; set; } = new();

    public HindiScript HindiScript { get; set; } = HindiScript.Roman;

    /// <summary>
    /// When the service in use hasn't answered after about this many seconds (a little more for long recordings),
    /// the next one is asked too and the first answer wins. 0 = only switch after a failure.
    /// </summary>
    public int HedgeSeconds { get; set; } = 4;

    /// <summary>Send compressed audio (AAC) to services that accept it: much smaller uploads.</summary>
    public bool CompressAudio { get; set; } = true;

    /// <summary>Shown with the cost estimate; prices per service are in this currency.</summary>
    public string Currency { get; set; } = "₹";

    public string ModelFor(EngineKind engine) => engine == EngineKind.Transcribe ? TranscribeModel : GenerateModel;

    /// <summary>The model a service uses (the Gemini services keep theirs in <see cref="TranscribeModel"/> / <see cref="GenerateModel"/>).</summary>
    public string ModelOf(SpeechService service) => service switch
    {
        SpeechService.Gemini => TranscribeModel,
        SpeechService.GeminiGeneral => GenerateModel,
        _ => Services[service].Model,
    };

    public string BaseUrlOf(SpeechService service) => service.IsGemini() ? ApiBaseUrl : Services[service].ApiBaseUrl;

    /// <summary>The services in <see cref="ServiceOrder"/>, main first.</summary>
    public IReadOnlyList<SpeechService> OrderedServices() =>
        SpeechServices.CleanOrder(ServiceOrder).Select(id => SpeechServices.ById(id)!.Value).ToList();
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
    /// <summary>The Gemini API key encrypted by the platform (Windows: DPAPI CurrentUser), base64. Never plain text.</summary>
    public string? ApiKeyProtected { get; set; }

    /// <summary>Keys of the other services by key slot (see <see cref="SpeechServices.KeySlot"/>), encrypted like <see cref="ApiKeyProtected"/>.</summary>
    public Dictionary<string, string> ApiKeysProtected { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Capture device id; null = system default.</summary>
    public string? MicrophoneId { get; set; }

    public bool StartWithWindows { get; set; }

    public bool FirstRunCompleted { get; set; }

    /// <summary>Where the user dragged the mini button: "x,y" in screen pixels (its bottom-centre); null = default.</summary>
    public string? MiniButtonPosition { get; set; }

    /// <summary>
    /// At the next dictation in the same app, look at the text box again and offer words the user corrected for the
    /// vocabulary. The text is only compared in memory, never stored or sent anywhere.
    /// </summary>
    public bool LearnFromCorrections { get; set; } = true;

    /// <summary>Transcribe on this device when there's no internet, no usable service or none answers in time.</summary>
    public bool OfflineEnabled { get; set; } = true;

    /// <summary>The offline model's id (see <see cref="Offline.OfflineModels"/>); null = this platform's default.</summary>
    public string? OfflineModel { get; set; }
}
