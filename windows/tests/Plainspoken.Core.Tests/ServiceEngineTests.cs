using System.Net;
using System.Text.RegularExpressions;
using Plainspoken.Core.Audio;
using Plainspoken.Core.Settings;
using Plainspoken.Core.Transcription;

namespace Plainspoken.Core.Tests;

public class ServiceEngineTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private static readonly RetryPolicy NoSleep = new(maxServerRetries: 1, delay: NoDelay.Delay);

    private static TranscriptionRequest Request(
        double seconds = 2.0,
        LanguagePreset languages = LanguagePreset.EnglishHindi,
        TranscriptionMode mode = TranscriptionMode.Smart,
        HindiScript script = HindiScript.Roman,
        string[]? vocabulary = null) =>
        new(WavEncoder.Encode(TestAudio.Speech(seconds)), seconds, mode, languages, vocabulary ?? ["WhatsApp", "New Delhi"], script);

    private static ServiceClient Client(FakeHttpHandler http, SpeechService service, string? baseUrl = null, ListLog? log = null) =>
        new(new HttpClient(http), service, baseUrl ?? service.Info().DefaultBaseUrl, () => "test-key", log ?? new ListLog(), NoSleep);

    private static SpeechServiceSettings Config(SpeechService service, Action<SpeechServiceSettings>? configure = null)
    {
        var c = new SpeechServiceSettings { Model = service.Info().DefaultModel, ApiBaseUrl = service.Info().DefaultBaseUrl };
        configure?.Invoke(c);
        return c;
    }

    private static FakeHttpHandler Answer(string body, HttpStatusCode status = HttpStatusCode.OK) => new((_, _) => FakeHttpHandler.Json(status, body));

    private static async Task<string> BodyOf(HttpContent content) => await content.ReadAsStringAsync();

    [Fact]
    public async Task Sarvam_posts_multipart_with_its_key_header_and_reads_the_transcript()
    {
        var http = Answer("""{"request_id":"r1","transcript":"kal meeting hai","language_code":"hi-IN"}""");
        var text = await new SarvamEngine(Client(http, SpeechService.Sarvam), Config(SpeechService.Sarvam), Timeout).TranscribeAsync(Request(), default);
        Assert.Equal("kal meeting hai", text);
        var r = Assert.Single(http.Requests);
        Assert.Equal("https://api.sarvam.ai/speech-to-text", r.Uri.AbsoluteUri);
        Assert.Equal("test-key", r.Headers["api-subscription-key"]);
        Assert.False(r.Headers.ContainsKey("Authorization"));
        var body = r.Text();
        Assert.Contains("name=\"model\"", body, StringComparison.Ordinal);
        Assert.Contains("saaras:v3", body, StringComparison.Ordinal);
        Assert.Contains("translit", body, StringComparison.Ordinal); // Hindi in Roman letters
        Assert.Contains("unknown", body, StringComparison.Ordinal); // language auto-detected for English + Hindi
        Assert.DoesNotContain("keyterms", body, StringComparison.Ordinal); // only with saaras:v4
    }

    [Fact]
    public async Task Sarvam_modes_and_key_terms_follow_the_settings()
    {
        var sarvam = Config(SpeechService.Sarvam);
        Assert.Equal("codemix", SarvamEngine.Mode(Request(script: HindiScript.Devanagari), sarvam));
        Assert.Equal("verbatim", SarvamEngine.Mode(Request(mode: TranscriptionMode.Verbatim), sarvam));
        Assert.Equal("transcribe", SarvamEngine.Mode(Request(languages: LanguagePreset.English), sarvam));
        Assert.Equal("codemix", SarvamEngine.Mode(Request(), Config(SpeechService.Sarvam, c => c.Option = "codemix")));
        Assert.Equal("en-IN", SarvamEngine.Language(LanguagePreset.English));
        var v4 = Config(SpeechService.Sarvam, c => c.Model = "saaras:v4");
        using var form = SarvamEngine.BuildBody(Request().AudioFor(SpeechService.Sarvam), Request(), v4);
        Assert.Contains("[\"WhatsApp\",\"New Delhi\"]", await BodyOf(form), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Long_recordings_are_split_for_Sarvams_30_second_limit_and_joined_in_order()
    {
        var http = Answer("""{"transcript":"part"}""");
        var text = await new SarvamEngine(Client(http, SpeechService.Sarvam), Config(SpeechService.Sarvam), Timeout).TranscribeAsync(Request(seconds: 70), default);
        Assert.Equal(3, http.Requests.Count);
        Assert.Equal("part part part", text);
    }

    [Fact]
    public void The_chunker_cuts_at_the_quietest_moment_before_the_limit()
    {
        var loud = TestAudio.Speech(40);
        Array.Clear(loud, 25 * 16000, 3200); // 200 ms of silence at 25 s
        var parts = AudioChunker.Split(loud, 16000, maxSeconds: 28.0);
        Assert.Equal(2, parts.Count);
        Assert.InRange(parts[0].Length, 25 * 16000, (25 * 16000) + 3200);
        Assert.Equal(loud.Length, parts.Sum(p => p.Length));
        Assert.Single(AudioChunker.Split(TestAudio.Speech(10), 16000, maxSeconds: 28.0));
    }

    [Fact]
    public async Task OpenAI_compatible_services_get_a_bearer_token_the_model_and_a_style_prompt()
    {
        var http = Answer("""{"text":"Kal meeting hai, please inform kar dena."}""");
        var engine = new OpenAIEngine(Client(http, SpeechService.OpenAI, "https://api.groq.com/openai/v1"),
            Config(SpeechService.OpenAI, c => c.Model = "whisper-large-v3-turbo"), Timeout);
        Assert.Equal("Kal meeting hai, please inform kar dena.", await engine.TranscribeAsync(Request(), default));
        var r = Assert.Single(http.Requests);
        Assert.Equal("https://api.groq.com/openai/v1/audio/transcriptions", r.Uri.AbsoluteUri);
        Assert.Equal("Bearer test-key", r.Headers["Authorization"]);
        var body = r.Text();
        Assert.Contains("whisper-large-v3-turbo", body, StringComparison.Ordinal);
        Assert.Contains("Kal meeting hai", body, StringComparison.Ordinal); // the Roman-script example in the prompt
        Assert.Contains("WhatsApp, New Delhi.", body, StringComparison.Ordinal);
        Assert.DoesNotContain("name=\"language\"", body, StringComparison.Ordinal);
        Assert.Equal("", OpenAIEngine.Prompt(Request(languages: LanguagePreset.English, vocabulary: []), Config(SpeechService.OpenAI)));
    }

    [Fact]
    public async Task Deepgram_sends_raw_audio_with_options_in_the_query()
    {
        var http = Answer("""{"results":{"channels":[{"alternatives":[{"transcript":"Hello there.","confidence":0.98}]}]}}""");
        Assert.Equal("Hello there.", await new DeepgramEngine(Client(http, SpeechService.Deepgram), Config(SpeechService.Deepgram), Timeout)
            .TranscribeAsync(Request(), default));
        var r = Assert.Single(http.Requests);
        var url = r.Uri.AbsoluteUri;
        Assert.StartsWith("https://api.deepgram.com/v1/listen?model=nova-3&smart_format=true&punctuate=true&language=multi", url, StringComparison.Ordinal);
        Assert.Contains("keyterm=WhatsApp", url, StringComparison.Ordinal);
        Assert.Contains("keyterm=New%20Delhi", url, StringComparison.Ordinal);
        Assert.Equal("Token test-key", r.Headers["Authorization"]);
        Assert.Equal("audio/wav", r.ContentType);
        var verbatim = DeepgramEngine.Query(Request(mode: TranscriptionMode.Verbatim, languages: LanguagePreset.English), Config(SpeechService.Deepgram));
        Assert.Contains(new KeyValuePair<string, string>("filler_words", "true"), verbatim);
        Assert.Contains(new KeyValuePair<string, string>("language", "en"), verbatim);
    }

    [Fact]
    public async Task ElevenLabs_asks_scribe_v2_to_drop_fillers_and_sends_hints_only_when_on()
    {
        var http = Answer("""{"language_code":"hin","language_probability":0.9,"text":"Theek hai.","words":[]}""");
        var engine = new ElevenLabsEngine(Client(http, SpeechService.ElevenLabs), Config(SpeechService.ElevenLabs, c => c.VocabularyHints = false), Timeout);
        Assert.Equal("Theek hai.", await engine.TranscribeAsync(Request(), default));
        var r = Assert.Single(http.Requests);
        Assert.Equal("https://api.elevenlabs.io/v1/speech-to-text", r.Uri.AbsoluteUri);
        Assert.Equal("test-key", r.Headers["xi-api-key"]);
        Assert.Contains("scribe_v2", r.Text(), StringComparison.Ordinal);
        Assert.Contains("no_verbatim", r.Text(), StringComparison.Ordinal);
        Assert.DoesNotContain("keyterms", r.Text(), StringComparison.Ordinal);
        using var withHints = ElevenLabsEngine.BuildBody(Request().AudioFor(SpeechService.ElevenLabs), Request(), Config(SpeechService.ElevenLabs));
        Assert.Equal(2, Regex.Count(await BodyOf(withHints), "name=\"keyterms\""));
    }

    [Fact]
    public void Service_errors_map_to_kinds_the_router_and_the_user_understand()
    {
        static TranscriptionErrorKind Kind(int status, string body) => ServiceErrors.FromResponse(SpeechService.Sarvam, status, body, null).Kind;
        Assert.Equal(TranscriptionErrorKind.InvalidKey, Kind(401, """{"error":{"message":"Invalid API subscription key"}}"""));
        Assert.Equal(TranscriptionErrorKind.NoCredits, Kind(402, "{}"));
        Assert.Equal(TranscriptionErrorKind.NoCredits, Kind(403, """{"error":{"message":"Insufficient credits"}}"""));
        Assert.Equal(TranscriptionErrorKind.NoCredits, Kind(429,
            """{"error":{"message":"You exceeded your current quota, please check your plan and billing details","type":"insufficient_quota"}}"""));
        Assert.Equal(TranscriptionErrorKind.RateLimited, Kind(429, """{"err_code":"TOO_MANY_REQUESTS","err_msg":"slow down"}"""));
        Assert.Equal(TranscriptionErrorKind.ModelNotFound, Kind(400, """{"detail":{"status":"invalid_model","message":"Model nova-9 not found"}}"""));
        Assert.Equal(TranscriptionErrorKind.Server, Kind(503, "<html>busy</html>"));
        Assert.Equal(TranscriptionErrorKind.Timeout, Kind(504, ""));
        Assert.Equal(TranscriptionErrorKind.BadRequest, Kind(413, """{"detail":"File too large"}"""));
        var e = ServiceErrors.FromResponse(SpeechService.Deepgram, 401, """{"err_msg":"Invalid credentials"}""", TimeSpan.FromSeconds(3));
        Assert.Equal(SpeechService.Deepgram, e.Service);
        Assert.Equal(TimeSpan.FromSeconds(3), e.RetryAfter);
    }

    [Fact]
    public async Task Server_errors_are_retried_and_the_key_never_leaves_its_host()
    {
        var http = new FakeHttpHandler((_, n) => n == 0 ? FakeHttpHandler.Json(HttpStatusCode.ServiceUnavailable, "{}") : FakeHttpHandler.Json(HttpStatusCode.OK, """{"text":"ok"}"""));
        Assert.Equal("ok", await new OpenAIEngine(Client(http, SpeechService.OpenAI), Config(SpeechService.OpenAI), Timeout).TranscribeAsync(Request(), default));
        Assert.Equal(2, http.Requests.Count);

        var other = Answer("{}");
        var c = Client(other, SpeechService.OpenAI);
        using var doc = await c.SendJsonAsync(() => new HttpRequestMessage(HttpMethod.Get, new Uri("https://elsewhere.example/x")), Timeout, "t", default);
        Assert.False(Assert.Single(other.Requests).Headers.ContainsKey("Authorization"));
    }

    [Fact]
    public async Task Missing_keys_and_odd_responses_fail_clearly()
    {
        var http = Answer("""{"unexpected":true}""");
        var noKey = new ServiceClient(new HttpClient(http), SpeechService.Sarvam, SpeechService.Sarvam.Info().DefaultBaseUrl, () => "  ", new ListLog());
        var e = await Assert.ThrowsAsync<TranscriptionException>(() => new SarvamEngine(noKey, Config(SpeechService.Sarvam), Timeout).TranscribeAsync(Request(), default));
        Assert.Equal(TranscriptionErrorKind.NoKey, e.Kind);
        Assert.Equal(SpeechService.Sarvam, e.Service);
        var odd = await Assert.ThrowsAsync<TranscriptionException>(() =>
            new SarvamEngine(Client(http, SpeechService.Sarvam), Config(SpeechService.Sarvam), Timeout).TranscribeAsync(Request(), default));
        Assert.Equal(TranscriptionErrorKind.UnexpectedResponse, odd.Kind);
    }

    [Fact]
    public async Task Logs_never_contain_any_services_key()
    {
        // Made-up key shapes, not real keys.
        var sarvamLike = "sk_" + "a1b2c3d4_" + new string('Q', 24);
        var openAILike = "sk-" + "proj-" + new string('Z', 30);
        var deepgramLike = string.Concat(Enumerable.Repeat("0123456789abcdef", 2)) + "01234567";
        var log = new ListLog();
        var http = Answer($$$"""{"error":{"message":"bad key {{{sarvamLike}}}, also Bearer {{{openAILike}}} and {{{deepgramLike}}}"}}""", HttpStatusCode.Unauthorized);
        var client = new ServiceClient(new HttpClient(http), SpeechService.Sarvam, SpeechService.Sarvam.Info().DefaultBaseUrl, () => sarvamLike, log, NoSleep);
        await Assert.ThrowsAsync<TranscriptionException>(() => new SarvamEngine(client, Config(SpeechService.Sarvam), Timeout).TranscribeAsync(Request(), default));
        Assert.NotEmpty(log.Lines);
        Assert.DoesNotContain(log.Lines, l => l.Contains(sarvamLike, StringComparison.Ordinal) || l.Contains(openAILike, StringComparison.Ordinal) ||
                                              l.Contains(deepgramLike, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Key_tests_use_a_free_endpoint_per_service()
    {
        var settings = new PlainspokenSettings().Normalize();
        foreach (var (service, url) in new[]
                 {
                     (SpeechService.Sarvam, "https://api.sarvam.ai/v2/models"),
                     (SpeechService.OpenAI, "https://api.openai.com/v1/models"),
                     (SpeechService.Deepgram, "https://api.deepgram.com/v1/projects"),
                     (SpeechService.ElevenLabs, "https://api.elevenlabs.io/v1/models"),
                 })
        {
            var http = Answer("{}");
            Assert.True((await KeyTester.TestAsync(new HttpClient(http), service, settings, "k", new ListLog(), default)).Ok);
            Assert.Equal(url, Assert.Single(http.Requests).Uri.AbsoluteUri);
        }

        var bad = Answer("""{"detail":{"status":"invalid_api_key","message":"Invalid API key"}}""", HttpStatusCode.Unauthorized);
        var result = await KeyTester.TestAsync(new HttpClient(bad), SpeechService.ElevenLabs, settings, "k", new ListLog(), default);
        Assert.False(result.Ok);
        Assert.Contains("ElevenLabs did not accept", result.Message, StringComparison.Ordinal);
        var noModels = Answer("{}", HttpStatusCode.NotFound);
        Assert.True((await KeyTester.TestAsync(new HttpClient(noModels), SpeechService.OpenAI, settings, "k", new ListLog(), default)).Ok); // e.g. a local Whisper server

        var cleanup = Answer("{}");
        Assert.True((await KeyTester.TestCleanupAsync(new HttpClient(cleanup), settings, "k", new ListLog(), default)).Ok);
        Assert.Equal("https://api.openai.com/v1/models", Assert.Single(cleanup.Requests).Uri.AbsoluteUri);
    }
}
