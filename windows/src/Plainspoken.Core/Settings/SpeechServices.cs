using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Plainspoken.Core.Settings;

/// <summary>The speech-to-text services Plainspoken can use. Settings files store <see cref="SpeechServices.Id"/>.</summary>
public enum SpeechService
{
    /// <summary>gemini-3.5-transcribe (Interactions API).</summary>
    Gemini,

    /// <summary>A general Gemini model with a transcription prompt.</summary>
    GeminiGeneral,

    /// <summary>Sarvam Saaras.</summary>
    Sarvam,

    /// <summary>Any OpenAI-style /audio/transcriptions API (OpenAI, Groq, a local Whisper server…).</summary>
    OpenAI,

    Deepgram,

    /// <summary>ElevenLabs Scribe.</summary>
    ElevenLabs,
}

/// <summary>What Plainspoken knows about each <see cref="SpeechService"/>.</summary>
public sealed record SpeechServiceInfo(
    string Id,
    string DisplayName,
    string KeySlot,
    string DefaultModel,
    string DefaultBaseUrl,
    bool CanStream,
    bool AcceptsCompressedAudio,
    int MaxRequestSeconds,
    bool CleansItself,
    string KeyUrl,
    string ShortName);

public static class SpeechServices
{
    public const string GeminiKey = "gemini";

    /// <summary>Key slot of the OpenAI-compatible clean-up service (it may differ from the speech one).</summary>
    public const string CleanupKey = "cleanup-openai";

    /// <summary>Main service plus up to two backups.</summary>
    public const int MaxServices = 3;

    private static readonly SpeechServiceInfo[] Infos =
    [
        new("gemini", "Gemini — transcribe model", GeminiKey, TranscriptionSettings.DefaultTranscribeModel, TranscriptionSettings.DefaultApiBaseUrl,
            CanStream: true, AcceptsCompressedAudio: false, MaxRequestSeconds: 0, CleansItself: true, "aistudio.google.com/apikey", "Gemini"),
        new("gemini-general", "Gemini — general model", GeminiKey, TranscriptionSettings.DefaultGenerateModel, TranscriptionSettings.DefaultApiBaseUrl,
            CanStream: false, AcceptsCompressedAudio: false, MaxRequestSeconds: 0, CleansItself: true, "aistudio.google.com/apikey", "Gemini"),
        new("sarvam", "Sarvam (Saaras)", "sarvam", "saaras:v3", "https://api.sarvam.ai",
            CanStream: true, AcceptsCompressedAudio: true, MaxRequestSeconds: 30, CleansItself: false, "dashboard.sarvam.ai", "Sarvam"),
        new("openai", "OpenAI-compatible", "openai", "gpt-4o-mini-transcribe", "https://api.openai.com/v1",
            CanStream: false, AcceptsCompressedAudio: true, MaxRequestSeconds: 600, CleansItself: false, "platform.openai.com/api-keys",
            "the OpenAI-compatible service"),
        new("deepgram", "Deepgram", "deepgram", "nova-3", "https://api.deepgram.com",
            CanStream: true, AcceptsCompressedAudio: true, MaxRequestSeconds: 0, CleansItself: false, "console.deepgram.com", "Deepgram"),
        new("elevenlabs", "ElevenLabs Scribe", "elevenlabs", "scribe_v2", "https://api.elevenlabs.io",
            CanStream: false, AcceptsCompressedAudio: true, MaxRequestSeconds: 0, CleansItself: false, "elevenlabs.io/app/settings/api-keys", "ElevenLabs"),
    ];

    public static IReadOnlyList<SpeechService> All { get; } = Enum.GetValues<SpeechService>();

    public static SpeechServiceInfo Info(this SpeechService service) => Infos[(int)service];

    public static string Id(this SpeechService service) => service.Info().Id;

    public static string DisplayName(this SpeechService service) => service.Info().DisplayName;

    /// <summary>Which saved key the service uses (both Gemini services share the Gemini key).</summary>
    public static string KeySlot(this SpeechService service) => service.Info().KeySlot;

    /// <summary>The name used in messages ("Sarvam took too long to answer").</summary>
    public static string ShortName(this SpeechService service) => service.Info().ShortName;

    public static bool IsGemini(this SpeechService service) => service.KeySlot() == GeminiKey;

    public static SpeechService? ById(string? id)
    {
        var trimmed = id?.Trim();
        for (var i = 0; i < Infos.Length; i++)
        {
            if (string.Equals(Infos[i].Id, trimmed, StringComparison.Ordinal))
            {
                return (SpeechService)i;
            }
        }

        return null;
    }

    /// <summary>Known ids only, no repeats, at most <see cref="MaxServices"/>; never empty.</summary>
    public static List<string> CleanOrder(IEnumerable<string?>? order)
    {
        var ids = (order ?? []).Select(ById).OfType<SpeechService>().Select(Id).Distinct(StringComparer.Ordinal).Take(MaxServices).ToList();
        return ids.Count > 0 ? ids : [SpeechService.Gemini.Id()];
    }
}

/// <summary>Settings of one speech service. The Gemini services keep their models and address in <see cref="TranscriptionSettings"/>.</summary>
public sealed class SpeechServiceSettings
{
    public string Model { get; set; } = string.Empty;

    public string ApiBaseUrl { get; set; } = string.Empty;

    /// <summary>Stream audio while the user speaks (only for services that can).</summary>
    public bool Streaming { get; set; } = true;

    /// <summary>Send the vocabulary as recognition hints (ElevenLabs charges 20 % more for it).</summary>
    public bool VocabularyHints { get; set; } = true;

    /// <summary>What one hour of audio costs on this service, in <see cref="TranscriptionSettings.Currency"/>. 0 = not set.</summary>
    public double PricePerHour { get; set; }

    /// <summary>A service-specific option; for Sarvam the mode (transcribe, codemix, translit, verbatim). Empty = automatic.</summary>
    public string Option { get; set; } = string.Empty;

    internal void Normalize(SpeechService service)
    {
        if (!service.IsGemini())
        {
            Model = string.IsNullOrWhiteSpace(Model) ? service.Info().DefaultModel : Model.Trim();
            var fallback = service.Info().DefaultBaseUrl;
            ApiBaseUrl = PlainspokenSettings.NormalizeBaseUrl(string.IsNullOrWhiteSpace(ApiBaseUrl) ? fallback : ApiBaseUrl, fallback);
        }
        else
        {
            Model ??= string.Empty;
            ApiBaseUrl ??= string.Empty;
        }

        PricePerHour = double.IsNaN(PricePerHour) || PricePerHour < 0 ? 0 : Math.Min(PricePerHour, 100_000);
        var option = Option?.Trim() ?? string.Empty;
        Option = option[..Math.Min(40, option.Length)];
    }
}

/// <summary>Per-service settings, one property per <see cref="SpeechService"/> so the file stays readable.</summary>
public sealed class ServiceSettingsMap
{
    public SpeechServiceSettings Gemini { get; set; } = new();

    public SpeechServiceSettings GeminiGeneral { get; set; } = new();

    public SpeechServiceSettings Sarvam { get; set; } = new() { PricePerHour = 30 };

    [JsonPropertyName("openai")]
    public SpeechServiceSettings OpenAI { get; set; } = new();

    public SpeechServiceSettings Deepgram { get; set; } = new();

    [JsonPropertyName("elevenlabs")]
    public SpeechServiceSettings ElevenLabs { get; set; } = new() { VocabularyHints = false };

    public SpeechServiceSettings this[SpeechService service] => service switch
    {
        SpeechService.Gemini => Gemini,
        SpeechService.GeminiGeneral => GeminiGeneral,
        SpeechService.Sarvam => Sarvam,
        SpeechService.OpenAI => OpenAI,
        SpeechService.Deepgram => Deepgram,
        SpeechService.ElevenLabs => ElevenLabs,
        _ => throw new ArgumentOutOfRangeException(nameof(service)),
    };

    internal void Normalize()
    {
        Gemini ??= new();
        GeminiGeneral ??= new();
        Sarvam ??= new() { PricePerHour = 30 };
        OpenAI ??= new();
        Deepgram ??= new();
        ElevenLabs ??= new() { VocabularyHints = false };
        foreach (var service in SpeechServices.All)
        {
            this[service].Normalize(service);
        }
    }
}

/// <summary>The Smart-mode clean-up step for services that return raw text.</summary>
public sealed class CleanupSettings
{
    public const string DefaultGeminiModel = "gemini-3.5-flash";
    public const string DefaultOpenAIModel = "gpt-4o-mini";
    public const string DefaultOpenAIBaseUrl = "https://api.openai.com/v1";

    public CleanupProvider Provider { get; set; } = CleanupProvider.Gemini;

    public string GeminiModel { get; set; } = DefaultGeminiModel;

    public string OpenaiModel { get; set; } = DefaultOpenAIModel;

    public string OpenaiBaseUrl { get; set; } = DefaultOpenAIBaseUrl;

    /// <summary>Also clean the Gemini services' text (they already tidy it themselves).</summary>
    public bool AlsoGemini { get; set; }

    internal void Normalize()
    {
        var gemini = GeminiModel?.Trim() ?? string.Empty;
        if (gemini.StartsWith("models/", StringComparison.Ordinal))
        {
            gemini = gemini["models/".Length..];
        }

        GeminiModel = gemini.Length == 0 ? DefaultGeminiModel : gemini;
        OpenaiModel = string.IsNullOrWhiteSpace(OpenaiModel) ? DefaultOpenAIModel : OpenaiModel.Trim();
        OpenaiBaseUrl = PlainspokenSettings.NormalizeBaseUrl(OpenaiBaseUrl, DefaultOpenAIBaseUrl);
    }
}

/// <summary>A spoken shortcut: saying <see cref="Trigger"/> types <see cref="Text"/>.</summary>
public sealed class Snippet
{
    public string Trigger { get; set; } = string.Empty;

    public string Text { get; set; } = string.Empty;
}

public static partial class SnippetCleaner
{
    public const int MaxSnippets = 100;
    public const int MaxTriggerLength = 60;
    public const int MaxTextLength = 2000;

    /// <summary>Trims, drops empty or over-long entries and repeated triggers, caps the list.</summary>
    public static List<Snippet> Clean(IEnumerable<Snippet?>? snippets)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<Snippet>();
        foreach (var s in snippets ?? [])
        {
            if (s is null)
            {
                continue;
            }

            var trigger = Spaces().Replace(s.Trigger?.Trim() ?? string.Empty, " ");
            var text = s.Text?.Trim() ?? string.Empty;
            if (trigger.Length == 0 || text.Length == 0 || trigger.Length > MaxTriggerLength || !seen.Add(trigger))
            {
                continue;
            }

            result.Add(new Snippet { Trigger = trigger, Text = text[..Math.Min(MaxTextLength, text.Length)] });
            if (result.Count == MaxSnippets)
            {
                break;
            }
        }

        return result;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
