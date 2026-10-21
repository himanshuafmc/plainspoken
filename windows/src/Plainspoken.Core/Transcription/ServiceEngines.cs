using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Plainspoken.Core.Audio;
using Plainspoken.Core.Settings;

namespace Plainspoken.Core.Transcription;

/// <summary>Helpers shared by the non-Google engines.</summary>
internal static class EngineSupport
{
    /// <summary>Time allowed for one request: the base timeout, plus a share of the audio length and upload size.</summary>
    public static TimeSpan RequestTimeout(TimeSpan baseTimeout, double seconds, int bytes) =>
        baseTimeout + TimeSpan.FromMilliseconds(seconds * 250) + TimeSpan.FromSeconds(bytes / (64.0 * 1024));

    /// <summary>Vocabulary terms a service accepts as hints: short, without odd characters, capped.</summary>
    public static List<string> HintTerms(IEnumerable<string> vocabulary, int max, int maxLength, int maxWords = 5) =>
        vocabulary
            .Select(t => t.Trim())
            .Where(t => t.Length > 0 && t.Length <= maxLength && t.Split(' ').Length <= maxWords && t.IndexOfAny(['<', '>', '{', '}', '[', ']', '\\', '"']) < 0)
            .Take(max)
            .ToList();

    /// <summary>
    /// A plain form field, written the way the services' own SDKs write it: quoted name, no per-part content type.
    /// (.NET would otherwise leave the name unquoted, which some servers' form parsers reject.)
    /// </summary>
    public static void AddField(this MultipartFormDataContent form, string name, string value)
    {
        var part = new StringContent(value, Encoding.UTF8);
        part.Headers.ContentType = null;
        part.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data") { Name = Quote(name) };
        form.Add(part);
    }

    /// <summary>The audio file part: quoted name and file name, without the extra filename* parameter .NET adds.</summary>
    public static void AddFile(this MultipartFormDataContent form, string name, EncodedAudio audio)
    {
        var part = new ByteArrayContent(audio.Bytes);
        part.Headers.ContentType = new MediaTypeHeaderValue(audio.MimeType);
        part.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data") { Name = Quote(name), FileName = Quote(audio.FileName) };
        form.Add(part);
    }

    private static string Quote(string s) => "\"" + s + "\"";

    public static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public static string JoinParts(IEnumerable<string> parts) =>
        string.Join(" ", parts.Select(p => p.Trim()).Where(p => p.Length > 0));

    /// <summary>Encodes query parameters (repeated keys allowed) onto <paramref name="url"/>.</summary>
    public static Uri WithQuery(Uri url, IEnumerable<KeyValuePair<string, string>> query)
    {
        var q = string.Join("&", query.Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value)));
        return new UriBuilder(url) { Query = q }.Uri;
    }

    public static string Format(double value) => value.ToString("0.0", CultureInfo.InvariantCulture);
}

/// <summary>
/// Sarvam Saaras over REST (POST /speech-to-text, multipart). One request accepts up to 30 s, so longer
/// recordings are split at quiet moments and the parts are transcribed in parallel (up to three at a time).
/// </summary>
public sealed class SarvamEngine(ServiceClient client, SpeechServiceSettings config, TimeSpan timeout) : ITranscriptionEngine
{
    public SpeechService Service => SpeechService.Sarvam;

    public async Task<string> TranscribeAsync(TranscriptionRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.DurationSeconds <= SpeechService.Sarvam.Info().MaxRequestSeconds - 1)
        {
            return await TranscribePartAsync(request.AudioFor(Service), request.DurationSeconds, request, ct).ConfigureAwait(false);
        }

        var (samples, rate) = WavEncoder.Decode(request.Wav);
        var parts = AudioChunker.Split(samples, rate, maxSeconds: 28.0);
        client.Log.Info($"sarvam: {EngineSupport.Format(request.DurationSeconds)}s split into {parts.Count} parts");
        using var permits = new SemaphoreSlim(3);
        var tasks = parts.Select(async part =>
        {
            await permits.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                return await TranscribePartAsync(EncodedAudio.FromWav(WavEncoder.Encode(part, rate)), WavEncoder.DurationSeconds(part.Length, rate), request, ct)
                    .ConfigureAwait(false);
            }
            finally
            {
                permits.Release();
            }
        }).ToList();
        var texts = await Task.WhenAll(tasks).ConfigureAwait(false);
        return EngineSupport.JoinParts(texts);
    }

    private async Task<string> TranscribePartAsync(EncodedAudio audio, double seconds, TranscriptionRequest request, CancellationToken ct)
    {
        using var doc = await client.SendJsonAsync(
            () => new HttpRequestMessage(HttpMethod.Post, client.Url("speech-to-text")) { Content = BuildBody(audio, request, config) },
            EngineSupport.RequestTimeout(timeout, Math.Min(seconds, 30), audio.Bytes.Length),
            "sarvam",
            ct).ConfigureAwait(false);
        return EngineSupport.Str(doc.RootElement, "transcript")?.Trim()
            ?? throw new TranscriptionException(TranscriptionErrorKind.UnexpectedResponse, "sarvam: " + JsonShape.Describe(doc.RootElement), service: Service);
    }

    public static MultipartFormDataContent BuildBody(EncodedAudio audio, TranscriptionRequest request, SpeechServiceSettings config)
    {
        ArgumentNullException.ThrowIfNull(audio);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(config);
        var form = new MultipartFormDataContent();
        form.AddFile("file", audio);
        form.AddField("model", config.Model);
        form.AddField("mode", Mode(request, config));
        form.AddField("language_code", Language(request.Languages));
        // Key terms only work with saaras:v4 (Sarvam SDK docs): up to 50, each up to 64 characters.
        if (config.VocabularyHints && config.Model.Contains("v4", StringComparison.Ordinal))
        {
            var terms = EngineSupport.HintTerms(request.Vocabulary, max: 50, maxLength: 64);
            if (terms.Count > 0)
            {
                form.AddField("keyterms", JsonSerializer.Serialize(terms));
            }
        }

        return form;
    }

    public static string Language(LanguagePreset preset) => preset == LanguagePreset.English ? "en-IN" : "unknown";

    /// <summary>translit = Hindi in Roman letters; codemix = English words in English, Hindi in Devanagari.</summary>
    public static string Mode(TranscriptionRequest request, SpeechServiceSettings config)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(config);
        if (config.Option.Length > 0)
        {
            return config.Option;
        }

        if (request.Mode == TranscriptionMode.Verbatim)
        {
            return "verbatim";
        }

        if (request.Languages == LanguagePreset.English)
        {
            return "transcribe";
        }

        return request.HindiScript == HindiScript.Roman ? "translit" : "codemix";
    }
}

/// <summary>
/// Any OpenAI-style transcription API: POST {base}/audio/transcriptions (OpenAI, Groq, a self-hosted Whisper server…).
/// Vocabulary and the wanted style go in the prompt; recordings over the size limit are split.
/// </summary>
public sealed class OpenAIEngine(ServiceClient client, SpeechServiceSettings config, TimeSpan timeout) : ITranscriptionEngine
{
    /// <summary>OpenAI's limit is 25 MB per file; stay under it.</summary>
    public const int MaxUploadBytes = 24 * 1024 * 1024;

    public SpeechService Service => SpeechService.OpenAI;

    public async Task<string> TranscribeAsync(TranscriptionRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var audio = request.AudioFor(Service);
        if (audio.Bytes.Length <= MaxUploadBytes)
        {
            return await TranscribePartAsync(audio, request.DurationSeconds, request, ct).ConfigureAwait(false);
        }

        var (samples, rate) = WavEncoder.Decode(request.Wav);
        var parts = AudioChunker.Split(samples, rate, maxSeconds: SpeechService.OpenAI.Info().MaxRequestSeconds, searchSeconds: 20.0);
        var texts = new List<string>();
        foreach (var part in parts)
        {
            texts.Add(await TranscribePartAsync(EncodedAudio.FromWav(WavEncoder.Encode(part, rate)), WavEncoder.DurationSeconds(part.Length, rate), request, ct)
                .ConfigureAwait(false));
        }

        return EngineSupport.JoinParts(texts);
    }

    private async Task<string> TranscribePartAsync(EncodedAudio audio, double seconds, TranscriptionRequest request, CancellationToken ct)
    {
        using var doc = await client.SendJsonAsync(
            () => new HttpRequestMessage(HttpMethod.Post, client.Url("audio/transcriptions")) { Content = BuildBody(audio, request, config) },
            EngineSupport.RequestTimeout(timeout, seconds, audio.Bytes.Length),
            "openai",
            ct).ConfigureAwait(false);
        return EngineSupport.Str(doc.RootElement, "text")?.Trim()
            ?? throw new TranscriptionException(TranscriptionErrorKind.UnexpectedResponse, "openai: " + JsonShape.Describe(doc.RootElement), service: Service);
    }

    public static MultipartFormDataContent BuildBody(EncodedAudio audio, TranscriptionRequest request, SpeechServiceSettings config)
    {
        ArgumentNullException.ThrowIfNull(audio);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(config);
        var form = new MultipartFormDataContent();
        form.AddFile("file", audio);
        form.AddField("model", config.Model);
        form.AddField("response_format", "json");
        form.AddField("temperature", "0");
        if (request.Languages == LanguagePreset.English)
        {
            form.AddField("language", "en");
        }

        var prompt = Prompt(request, config);
        if (prompt.Length > 0)
        {
            form.AddField("prompt", prompt);
        }

        return form;
    }

    /// <summary>
    /// Whisper-style models copy the style of the prompt, so it shows the wanted script (and fillers for Verbatim),
    /// followed by names to spell correctly. Kept short (these prompts are limited to about 200 tokens).
    /// </summary>
    public static string Prompt(TranscriptionRequest request, SpeechServiceSettings config)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(config);
        var parts = new List<string>();
        if (request.Mode == TranscriptionMode.Verbatim)
        {
            parts.Add("Umm, so, uh, I was like, hmm, thinking.");
        }

        if (request.Languages != LanguagePreset.English)
        {
            switch (request.HindiScript)
            {
                case HindiScript.Roman:
                    parts.Add("Kal meeting hai, please sabko inform kar dena.");
                    break;
                case HindiScript.Devanagari:
                    parts.Add("कल meeting है, please सबको inform कर देना।");
                    break;
            }
        }

        if (config.VocabularyHints)
        {
            var terms = EngineSupport.HintTerms(request.Vocabulary, max: 40, maxLength: 40);
            if (terms.Count > 0)
            {
                parts.Add(string.Join(", ", terms) + ".");
            }
        }

        var prompt = string.Join(" ", parts);
        return prompt.Length <= 600 ? prompt : prompt[..600];
    }
}

/// <summary>Deepgram pre-recorded transcription: POST /v1/listen with the audio as the body and options in the query.</summary>
public sealed class DeepgramEngine(ServiceClient client, SpeechServiceSettings config, TimeSpan timeout) : ITranscriptionEngine
{
    public SpeechService Service => SpeechService.Deepgram;

    public async Task<string> TranscribeAsync(TranscriptionRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var audio = request.AudioFor(Service);
        var url = EngineSupport.WithQuery(client.Url("v1/listen"), Query(request, config));
        using var doc = await client.SendJsonAsync(
            () =>
            {
                var content = new ByteArrayContent(audio.Bytes);
                content.Headers.ContentType = new MediaTypeHeaderValue(audio.MimeType);
                return new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
            },
            EngineSupport.RequestTimeout(timeout, request.DurationSeconds, audio.Bytes.Length),
            "deepgram",
            ct).ConfigureAwait(false);
        return Parse(doc.RootElement)
            ?? throw new TranscriptionException(TranscriptionErrorKind.UnexpectedResponse, "deepgram: " + JsonShape.Describe(doc.RootElement), service: Service);
    }

    /// <summary>Query options shared by the REST and streaming endpoints (keyterm may repeat).</summary>
    public static List<KeyValuePair<string, string>> Query(TranscriptionRequest request, SpeechServiceSettings config)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(config);
        var q = new List<KeyValuePair<string, string>>
        {
            new("model", config.Model),
            new("smart_format", "true"),
            new("punctuate", "true"),
            new("language", Language(request.Languages)),
        };
        if (request.Mode == TranscriptionMode.Verbatim)
        {
            q.Add(new("filler_words", "true"));
        }

        // Key term prompting works with Nova-3 only.
        if (config.VocabularyHints && config.Model.StartsWith("nova-3", StringComparison.Ordinal))
        {
            q.AddRange(EngineSupport.HintTerms(request.Vocabulary, max: 100, maxLength: 50).Select(t => new KeyValuePair<string, string>("keyterm", t)));
        }

        return q;
    }

    /// <summary>"multi" lets Nova-3 follow English and Hindi switching mid-sentence.</summary>
    public static string Language(LanguagePreset preset) => preset == LanguagePreset.English ? "en" : "multi";

    /// <summary>results.channels[0].alternatives[0].transcript</summary>
    public static string? Parse(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Object &&
            results.TryGetProperty("channels", out var channels) && channels.ValueKind == JsonValueKind.Array && channels.GetArrayLength() > 0 &&
            channels[0].ValueKind == JsonValueKind.Object &&
            channels[0].TryGetProperty("alternatives", out var alternatives) && alternatives.ValueKind == JsonValueKind.Array &&
            alternatives.GetArrayLength() > 0)
        {
            return EngineSupport.Str(alternatives[0], "transcript")?.Trim();
        }

        return null;
    }
}

/// <summary>ElevenLabs Scribe: POST /v1/speech-to-text (multipart). With scribe_v2, Smart mode asks it to leave out fillers too.</summary>
public sealed class ElevenLabsEngine(ServiceClient client, SpeechServiceSettings config, TimeSpan timeout) : ITranscriptionEngine
{
    public SpeechService Service => SpeechService.ElevenLabs;

    public async Task<string> TranscribeAsync(TranscriptionRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var audio = request.AudioFor(Service);
        using var doc = await client.SendJsonAsync(
            () => new HttpRequestMessage(HttpMethod.Post, client.Url("v1/speech-to-text")) { Content = BuildBody(audio, request, config) },
            EngineSupport.RequestTimeout(timeout, request.DurationSeconds, audio.Bytes.Length),
            "elevenlabs",
            ct).ConfigureAwait(false);
        return EngineSupport.Str(doc.RootElement, "text")?.Trim()
            ?? throw new TranscriptionException(TranscriptionErrorKind.UnexpectedResponse, "elevenlabs: " + JsonShape.Describe(doc.RootElement), service: Service);
    }

    public static MultipartFormDataContent BuildBody(EncodedAudio audio, TranscriptionRequest request, SpeechServiceSettings config)
    {
        ArgumentNullException.ThrowIfNull(audio);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(config);
        var form = new MultipartFormDataContent();
        form.AddField("model_id", config.Model);
        form.AddFile("file", audio);
        form.AddField("tag_audio_events", "false");
        if (request.Languages == LanguagePreset.English)
        {
            form.AddField("language_code", "en");
        }

        if (request.Mode == TranscriptionMode.Smart && config.Model.Contains("v2", StringComparison.Ordinal))
        {
            form.AddField("no_verbatim", "true");
        }

        // Key terms cost 20 % more on ElevenLabs, so they are off unless switched on; over 100 adds a minimum charge.
        if (config.VocabularyHints)
        {
            foreach (var term in EngineSupport.HintTerms(request.Vocabulary, max: 100, maxLength: 49))
            {
                form.AddField("keyterms", term);
            }
        }

        return form;
    }
}
