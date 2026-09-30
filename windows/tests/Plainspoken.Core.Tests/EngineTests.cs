using System.Net;
using System.Text.Json.Nodes;
using Plainspoken.Core.Audio;
using Plainspoken.Core.Logging;
using Plainspoken.Core.Settings;
using Plainspoken.Core.Transcription;

namespace Plainspoken.Core.Tests;

public class EngineTests
{
    private const string Key = "test-key-not-real";

    private static TranscriptionRequest Request(int seconds = 1) =>
        new(WavEncoder.Encode(new short[16000 * seconds]), seconds, TranscriptionMode.Smart, LanguagePreset.EnglishHindi, ["WhatsApp"]);

    private static RetryPolicy FastRetry() => new(delay: NoDelay.Delay, random: new Random(1));

    private static GeminiTranscribeEngine Transcribe(GeminiFake fake, ILog? log = null, Func<string?>? key = null) =>
        new(new GeminiClient(fake.Client(), TranscriptionSettings.DefaultApiBaseUrl, key ?? (() => Key), log ?? NullLog.Instance, FastRetry()),
            "gemini-3.5-transcribe", TimeSpan.FromSeconds(30));

    private static TranscriptionRequest LargeRequest() =>
        new(new byte[GeminiTranscribeEngine.InlineLimitBytes + 1], 330, TranscriptionMode.Smart, LanguagePreset.EnglishHindi, ["WhatsApp"]);

    [Fact]
    public async Task Short_clip_is_sent_inline_in_one_request()
    {
        var fake = new GeminiFake();
        var request = Request();

        var text = await Transcribe(fake).TranscribeAsync(request, CancellationToken.None);

        Assert.StartsWith("So, please add milk", text, StringComparison.Ordinal);
        var call = Assert.Single(fake.Handler.Requests);
        Assert.Equal("https://generativelanguage.googleapis.com/v1beta/interactions", call.Uri.AbsoluteUri);
        Assert.Equal(Key, call.Headers["x-goog-api-key"]);
        var input = JsonNode.Parse(call.Body!)!["input"]![0]!;
        Assert.Equal(Convert.ToBase64String(request.Wav), input["data"]!.GetValue<string>());
        Assert.Equal("audio/wav", input["mime_type"]!.GetValue<string>());
        Assert.Null(input["uri"]);
    }

    [Fact]
    public async Task Inline_rejection_falls_back_to_file_upload()
    {
        var fake = new GeminiFake();
        fake.Override = r => r.Uri.AbsolutePath == "/v1beta/interactions" && r.Body!.Contains("\"data\"", StringComparison.Ordinal)
            ? FakeHttpHandler.Json(HttpStatusCode.BadRequest, """{"error":{"message":"Inline audio is not supported for this model.","code":"invalid_request"}}""")
            : null;
        var engine = Transcribe(fake);

        var text = await engine.TranscribeAsync(Request(), CancellationToken.None);
        await engine.CleanupTask;

        Assert.StartsWith("So, please", text, StringComparison.Ordinal);
        Assert.Equal(2, fake.To("/v1beta/interactions").Count());
        Assert.Single(fake.To("upload_id"));
        Assert.Single(fake.Handler.Requests, r => r.Method == HttpMethod.Delete);
    }

    [Fact]
    public async Task Long_clip_uses_upload_transcribe_delete()
    {
        var fake = new GeminiFake();
        var engine = Transcribe(fake);
        var request = LargeRequest();

        var text = await engine.TranscribeAsync(request, CancellationToken.None);
        await engine.CleanupTask;

        Assert.StartsWith("So, please add milk", text, StringComparison.Ordinal);
        var calls = fake.Handler.Requests;
        Assert.Equal(4, calls.Count);

        var start = calls[0];
        Assert.Equal("https://generativelanguage.googleapis.com/upload/v1beta/files", start.Uri.AbsoluteUri);
        Assert.Equal("resumable", start.Headers["X-Goog-Upload-Protocol"]);
        Assert.Equal("start", start.Headers["X-Goog-Upload-Command"]);
        Assert.Equal(request.Wav.Length.ToString(System.Globalization.CultureInfo.InvariantCulture), start.Headers["X-Goog-Upload-Header-Content-Length"]);
        Assert.Equal("audio/wav", start.Headers["X-Goog-Upload-Header-Content-Type"]);
        Assert.Equal(Key, start.Headers["x-goog-api-key"]);

        var bytes = calls[1];
        Assert.Equal(GeminiFake.UploadUrl, bytes.Uri.AbsoluteUri);
        Assert.Equal("upload, finalize", bytes.Headers["X-Goog-Upload-Command"]);
        Assert.Equal("0", bytes.Headers["X-Goog-Upload-Offset"]);
        Assert.Equal(request.Wav.Length, bytes.Bytes!.Length);

        var post = calls[2];
        Assert.Equal("https://generativelanguage.googleapis.com/v1beta/interactions", post.Uri.AbsoluteUri);
        var body = JsonNode.Parse(post.Body!)!;
        Assert.Equal("https://generativelanguage.googleapis.com/v1beta/files/1qp4xte94dib", body["input"]![0]!["uri"]!.GetValue<string>());
        Assert.Equal("WhatsApp", body["generation_config"]!["transcription_config"]!["custom_vocabulary"]![0]!.GetValue<string>());

        Assert.Equal(HttpMethod.Delete, calls[3].Method);
        Assert.Equal("https://generativelanguage.googleapis.com/v1beta/files/1qp4xte94dib", calls[3].Uri.AbsoluteUri);
    }

    [Fact]
    public async Task No_key_fails_before_any_request()
    {
        var fake = new GeminiFake();
        var ex = await Assert.ThrowsAsync<TranscriptionException>(() => Transcribe(fake, key: () => null).TranscribeAsync(Request(), CancellationToken.None));
        Assert.Equal(TranscriptionErrorKind.NoKey, ex.Kind);
        Assert.Empty(fake.Handler.Requests);
    }

    [Fact]
    public async Task Server_errors_are_retried_twice_then_succeed()
    {
        var fake = new GeminiFake();
        var failures = 0;
        fake.Override = r => r.Uri.AbsolutePath == "/v1beta/interactions" && failures++ < 2
            ? FakeHttpHandler.Json(HttpStatusCode.ServiceUnavailable, """{"error":{"code":503,"message":"overloaded","status":"UNAVAILABLE"}}""")
            : null;

        var text = await Transcribe(fake).TranscribeAsync(Request(), CancellationToken.None);

        Assert.StartsWith("So, please", text, StringComparison.Ordinal);
        Assert.Equal(3, fake.To("/v1beta/interactions").Count());
        Assert.Empty(fake.To("upload")); // short clips are inline: nothing to upload
    }

    [Fact]
    public async Task Server_errors_give_up_after_three_attempts_and_still_delete_the_file()
    {
        var fake = new GeminiFake();
        fake.Override = r => r.Uri.AbsolutePath == "/v1beta/interactions"
            ? FakeHttpHandler.Json(HttpStatusCode.InternalServerError, "{}")
            : null;
        var engine = Transcribe(fake);

        var ex = await Assert.ThrowsAsync<TranscriptionException>(() => engine.TranscribeAsync(LargeRequest(), CancellationToken.None));
        await engine.CleanupTask;

        Assert.Equal(TranscriptionErrorKind.Server, ex.Kind);
        Assert.Equal(3, fake.To("/v1beta/interactions").Count());
        Assert.Single(fake.To("upload_id")); // uploaded once, not per retry
        Assert.Single(fake.Handler.Requests, r => r.Method == HttpMethod.Delete);
    }

    [Fact]
    public async Task Rate_limit_is_not_retried_by_the_engine()
    {
        var fake = new GeminiFake();
        fake.Override = r => r.Uri.AbsolutePath == "/v1beta/interactions"
            ? FakeHttpHandler.Json(HttpStatusCode.TooManyRequests, Fixtures.Read("error-429-per-minute.json"), ("Retry-After", "43"))
            : null;

        var ex = await Assert.ThrowsAsync<TranscriptionException>(() => Transcribe(fake).TranscribeAsync(Request(), CancellationToken.None));

        Assert.Equal(TranscriptionErrorKind.RateLimited, ex.Kind);
        Assert.Equal(TimeSpan.FromSeconds(43), ex.RetryAfter);
        Assert.Single(fake.To("/v1beta/interactions"));
    }

    [Fact]
    public async Task Network_failure_is_reported_as_network()
    {
        var fake = new GeminiFake();
        fake.Override = _ => throw new HttpRequestException(HttpRequestError.NameResolutionError, "no dns");
        var ex = await Assert.ThrowsAsync<TranscriptionException>(() => Transcribe(fake).TranscribeAsync(Request(), CancellationToken.None));
        Assert.Equal(TranscriptionErrorKind.Network, ex.Kind);
    }

    [Fact]
    public async Task Hanging_request_times_out_but_user_cancel_is_a_cancel()
    {
        var hang = new FakeHttpHandlerThatHangs();
        var client = new GeminiClient(new HttpClient(hang), TranscriptionSettings.DefaultApiBaseUrl, () => Key, NullLog.Instance, FastRetry());
        var engine = new GeminiTranscribeEngine(client, "gemini-3.5-transcribe", TimeSpan.FromMilliseconds(200));

        var timeout = await Assert.ThrowsAsync<TranscriptionException>(() => engine.TranscribeAsync(Request(), CancellationToken.None));
        Assert.Equal(TranscriptionErrorKind.Timeout, timeout.Kind);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        var slow = new GeminiTranscribeEngine(client, "gemini-3.5-transcribe", TimeSpan.FromSeconds(30));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => slow.TranscribeAsync(Request(), cts.Token));
    }

    [Fact]
    public async Task Processing_file_is_polled_until_active()
    {
        var fake = new GeminiFake();
        var polls = 0;
        fake.Override = r =>
        {
            if (r.Uri.Query.Contains("upload_id", StringComparison.Ordinal))
            {
                return FakeHttpHandler.Json(HttpStatusCode.OK, Fixtures.Read("upload-response.json").Replace("\"ACTIVE\"", "\"PROCESSING\"", StringComparison.Ordinal));
            }

            if (r.Method == HttpMethod.Get && r.Uri.AbsolutePath == "/v1beta/files/1qp4xte94dib")
            {
                polls++;
                var state = polls < 2 ? "PROCESSING" : "ACTIVE";
                return FakeHttpHandler.Json(HttpStatusCode.OK, $$"""{"name":"files/1qp4xte94dib","uri":"https://generativelanguage.googleapis.com/v1beta/files/1qp4xte94dib","state":"{{state}}"}""");
            }

            return null;
        };

        var text = await Transcribe(fake).TranscribeAsync(LargeRequest(), CancellationToken.None);
        Assert.StartsWith("So, please", text, StringComparison.Ordinal);
        Assert.Equal(2, polls);
    }

    [Fact]
    public async Task In_progress_interaction_is_polled()
    {
        var fake = new GeminiFake();
        fake.Override = r =>
        {
            if (r.Method == HttpMethod.Post && r.Uri.AbsolutePath == "/v1beta/interactions")
            {
                return FakeHttpHandler.Json(HttpStatusCode.OK, """{"id":"abc","status":"in_progress"}""");
            }

            if (r.Method == HttpMethod.Get && r.Uri.AbsolutePath == "/v1beta/interactions/abc")
            {
                return FakeHttpHandler.Json(HttpStatusCode.OK, Fixtures.Read("interactions-response.json"));
            }

            return null;
        };

        Assert.StartsWith("So, please", await Transcribe(fake).TranscribeAsync(Request(), CancellationToken.None), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Completed_with_no_text_returns_empty_and_unknown_shape_throws()
    {
        var fake = new GeminiFake();
        fake.Override = r => r.Uri.AbsolutePath == "/v1beta/interactions"
            ? FakeHttpHandler.Json(HttpStatusCode.OK, """{"status":"completed","steps":[{"type":"model_output","content":[]}]}""")
            : null;
        Assert.Equal("", await Transcribe(fake).TranscribeAsync(Request(), CancellationToken.None));

        var log = new ListLog();
        var odd = new GeminiFake();
        odd.Override = r => r.Uri.AbsolutePath == "/v1beta/interactions"
            ? FakeHttpHandler.Json(HttpStatusCode.OK, """{"status":"completed","transcript":{"value":"private words"}}""")
            : null;
        var ex = await Assert.ThrowsAsync<TranscriptionException>(() => Transcribe(odd, log).TranscribeAsync(Request(), CancellationToken.None));
        Assert.Equal(TranscriptionErrorKind.UnexpectedResponse, ex.Kind);
        Assert.Contains("transcript:{value:str(13)}", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(log.Lines, l => l.Contains("private", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Key_is_only_sent_to_the_api_host()
    {
        var fake = new GeminiFake();
        fake.Override = r => r.Uri.AbsolutePath == "/upload/v1beta/files" && r.Uri.Query.Length == 0
            ? FakeHttpHandler.Json(HttpStatusCode.OK, "", ("X-Goog-Upload-URL", "https://evil.example/upload?upload_id=1"))
            : r.Uri.Host == "evil.example" ? FakeHttpHandler.Json(HttpStatusCode.OK, Fixtures.Read("upload-response.json")) : null;

        await Transcribe(fake).TranscribeAsync(LargeRequest(), CancellationToken.None);

        Assert.False(fake.To("evil.example").Single().Headers.ContainsKey("x-goog-api-key"));
    }

    [Fact]
    public async Task Proxy_mode_sends_no_key_header()
    {
        var fake = new GeminiFake();
        var client = new GeminiClient(fake.Client(), TranscriptionSettings.DefaultApiBaseUrl, () => null, NullLog.Instance, FastRetry(), requireApiKey: false);
        await new GeminiTranscribeEngine(client, "gemini-3.5-transcribe", TimeSpan.FromSeconds(5)).TranscribeAsync(Request(), CancellationToken.None);
        Assert.All(fake.Handler.Requests, r => Assert.False(r.Headers.ContainsKey("x-goog-api-key")));
    }

    [Fact]
    public async Task Generate_engine_sends_inline_audio_to_the_configured_model()
    {
        var fake = new GeminiFake();
        var client = new GeminiClient(fake.Client(), TranscriptionSettings.DefaultApiBaseUrl, () => Key, NullLog.Instance, FastRetry());
        var engine = new GeminiGenerateEngine(client, "models/gemini-3.5-flash-lite", TimeSpan.FromSeconds(5));

        var text = await engine.TranscribeAsync(Request(), CancellationToken.None);

        Assert.StartsWith("So, please add milk", text, StringComparison.Ordinal);
        var call = Assert.Single(fake.Handler.Requests);
        Assert.Equal("https://generativelanguage.googleapis.com/v1beta/models/gemini-3.5-flash-lite:generateContent", call.Uri.AbsoluteUri);
        Assert.Equal(Key, call.Headers["x-goog-api-key"]);
    }

    [Fact]
    public async Task Generate_engine_uploads_large_clips()
    {
        var fake = new GeminiFake();
        var client = new GeminiClient(fake.Client(), TranscriptionSettings.DefaultApiBaseUrl, () => Key, NullLog.Instance, FastRetry());
        var engine = new GeminiGenerateEngine(client, "gemini-3.5-flash-lite", TimeSpan.FromSeconds(5));
        var big = new TranscriptionRequest(new byte[GeminiGenerateEngine.InlineLimitBytes + 1], 460, TranscriptionMode.Smart, LanguagePreset.Auto, []);

        await engine.TranscribeAsync(big, CancellationToken.None);
        await engine.CleanupTask;

        var gen = Assert.Single(fake.To(":generateContent"));
        Assert.Contains("fileData", gen.Body!, StringComparison.Ordinal);
        Assert.DoesNotContain("inlineData", gen.Body!, StringComparison.Ordinal);
        Assert.Single(fake.Handler.Requests, r => r.Method == HttpMethod.Delete);
    }

    [Fact]
    public async Task Key_tester_reports_good_bad_and_missing_model()
    {
        var ok = new GeminiFake();
        Assert.True((await KeyTester.TestAsync(ok.Client(), TranscriptionSettings.DefaultApiBaseUrl, "gemini-3.5-transcribe", Key, NullLog.Instance, default)).Ok);
        Assert.Equal("https://generativelanguage.googleapis.com/v1beta/models/gemini-3.5-transcribe", ok.Handler.Requests.Single().Uri.AbsoluteUri);

        var bad = new GeminiFake { Override = _ => FakeHttpHandler.Json(HttpStatusCode.BadRequest, Fixtures.Read("error-400-invalid-key.json")) };
        var badResult = await KeyTester.TestAsync(bad.Client(), TranscriptionSettings.DefaultApiBaseUrl, "gemini-3.5-transcribe", Key, NullLog.Instance, default);
        Assert.False(badResult.Ok);
        Assert.Contains("did not accept", badResult.Message, StringComparison.Ordinal);

        var missing = new GeminiFake { Override = _ => FakeHttpHandler.Json(HttpStatusCode.NotFound, """{"error":{"code":404,"message":"not found"}}""") };
        Assert.Contains("isn't available", (await KeyTester.TestAsync(missing.Client(), TranscriptionSettings.DefaultApiBaseUrl, "x", Key, NullLog.Instance, default)).Message, StringComparison.Ordinal);

        Assert.False((await KeyTester.TestAsync(ok.Client(), TranscriptionSettings.DefaultApiBaseUrl, "x", " ", NullLog.Instance, default)).Ok);
    }

    private sealed class FakeHttpHandlerThatHangs : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("unreachable");
        }
    }
}

public class RetryPolicyTests
{
    [Fact]
    public void Backoff_is_exponential_with_bounded_jitter()
    {
        var policy = new RetryPolicy(baseDelay: TimeSpan.FromSeconds(1), jitter: 0.5, random: new Random(7));
        for (var i = 0; i < 50; i++)
        {
            Assert.InRange(policy.BackoffFor(1).TotalMilliseconds, 500, 1500);
            Assert.InRange(policy.BackoffFor(2).TotalMilliseconds, 1000, 3000);
        }

        var noJitter = new RetryPolicy(baseDelay: TimeSpan.FromSeconds(1), jitter: 0);
        Assert.Equal(TimeSpan.FromSeconds(1), noJitter.BackoffFor(1));
        Assert.Equal(TimeSpan.FromSeconds(2), noJitter.BackoffFor(2));
        Assert.Equal(TimeSpan.FromSeconds(4), noJitter.BackoffFor(3));
    }

    [Fact]
    public async Task Retries_only_server_errors_and_waits_between_attempts()
    {
        var waits = new List<TimeSpan>();
        var policy = new RetryPolicy(jitter: 0, delay: (t, _) => { waits.Add(t); return Task.CompletedTask; });

        var attempts = 0;
        var ex = await Assert.ThrowsAsync<TranscriptionException>(() => policy.ExecuteAsync<int>("op", _ =>
        {
            attempts++;
            throw new TranscriptionException(TranscriptionErrorKind.Server, "boom", 500);
        }, NullLog.Instance, default));
        Assert.Equal(3, attempts);
        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)], waits);

        attempts = 0;
        await Assert.ThrowsAsync<TranscriptionException>(() => policy.ExecuteAsync<int>("op", _ =>
        {
            attempts++;
            throw new TranscriptionException(TranscriptionErrorKind.InvalidKey, "no", 401);
        }, NullLog.Instance, default));
        Assert.Equal(1, attempts);
    }
}

public class TranscriptionServiceTests
{
    private sealed class ScriptedEngine(EngineKind kind, Func<int, string> behaviour) : ITranscriptionEngine
    {
        public int Calls { get; private set; }

        public EngineKind Kind => kind;

        public Task<string> TranscribeAsync(TranscriptionRequest request, CancellationToken ct) =>
            Task.FromResult(behaviour(++Calls));
    }

    private static TranscriptionRequest Req() => new([1], 1, TranscriptionMode.Smart, LanguagePreset.Auto, []);

    private static TranscriptionException Limited(TranscriptionErrorKind kind = TranscriptionErrorKind.RateLimited, double retry = 20) =>
        new(kind, "429", 429, TimeSpan.FromSeconds(retry));

    [Fact]
    public async Task Uses_the_selected_engine()
    {
        var t = new ScriptedEngine(EngineKind.Transcribe, _ => "from transcribe");
        var g = new ScriptedEngine(EngineKind.Generate, _ => "from generate");
        var service = new TranscriptionService((k, _) => k == EngineKind.Transcribe ? t : g, NullLog.Instance);

        var settings = new PlainspokenSettings { Transcription = { Engine = EngineKind.Generate } };
        var outcome = await service.TranscribeAsync(Req(), settings, null, default);

        Assert.Equal("from generate", outcome.Text);
        Assert.Equal(0, t.Calls);
    }

    [Fact]
    public async Task Rate_limit_falls_back_to_the_backup_engine()
    {
        var t = new ScriptedEngine(EngineKind.Transcribe, _ => throw Limited());
        var g = new ScriptedEngine(EngineKind.Generate, _ => "backup text");
        var statuses = new List<string>();
        var service = new TranscriptionService((k, _) => k == EngineKind.Transcribe ? t : g, NullLog.Instance, NoDelay.Delay);

        var outcome = await service.TranscribeAsync(Req(), new PlainspokenSettings(), new SyncProgress(statuses), default);

        Assert.Equal("backup text", outcome.Text);
        Assert.Equal(EngineKind.Generate, outcome.Engine);
        Assert.Contains(statuses, s => s.Contains("backup", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Without_backup_a_short_per_minute_limit_waits_and_retries_once()
    {
        var waits = new List<TimeSpan>();
        var t = new ScriptedEngine(EngineKind.Transcribe, n => n == 1 ? throw Limited(retry: 20) : "second try");
        var service = new TranscriptionService((_, _) => t, NullLog.Instance, (d, _) => { waits.Add(d); return Task.CompletedTask; });
        var settings = new PlainspokenSettings { Transcription = { UseBackupEngineWhenLimited = false } };

        var outcome = await service.TranscribeAsync(Req(), settings, null, default);

        Assert.Equal("second try", outcome.Text);
        Assert.Equal([TimeSpan.FromSeconds(21)], waits);
    }

    [Fact]
    public async Task Daily_quota_is_reported_when_backup_also_fails()
    {
        var t = new ScriptedEngine(EngineKind.Transcribe, _ => throw Limited(TranscriptionErrorKind.DailyQuota, 40000));
        var g = new ScriptedEngine(EngineKind.Generate, _ => throw new TranscriptionException(TranscriptionErrorKind.Network, "down"));
        var service = new TranscriptionService((k, _) => k == EngineKind.Transcribe ? t : g, NullLog.Instance, NoDelay.Delay);

        var ex = await Assert.ThrowsAsync<TranscriptionException>(() => service.TranscribeAsync(Req(), new PlainspokenSettings(), null, default));

        Assert.Equal(TranscriptionErrorKind.DailyQuota, ex.Kind);
        Assert.Equal(1, t.Calls);
        Assert.Equal(1, g.Calls);
    }

    [Fact]
    public async Task Warm_up_calls_the_model_once_per_interval_and_never_throws()
    {
        var calls = 0;
        var service = new TranscriptionService((_, _) => new ScriptedEngine(EngineKind.Transcribe, _ => "x"), NullLog.Instance,
            warmUp: (_, _) => { Interlocked.Increment(ref calls); throw new HttpRequestException("offline"); });

        service.WarmUp(new PlainspokenSettings());
        service.WarmUp(new PlainspokenSettings()); // within 20 s: ignored
        await Task.Delay(200);

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Gemini_warm_up_is_a_metadata_get_for_the_selected_model()
    {
        var fake = new GeminiFake();
        var warmUp = TranscriptionService.GeminiWarmUp(fake.Client(), () => "k", NullLog.Instance);
        await warmUp(new PlainspokenSettings(), CancellationToken.None);
        var call = Assert.Single(fake.Handler.Requests);
        Assert.Equal(HttpMethod.Get, call.Method);
        Assert.Equal("https://generativelanguage.googleapis.com/v1beta/models/gemini-3.5-transcribe", call.Uri.AbsoluteUri);
    }

    [Fact]
    public async Task Other_errors_do_not_trigger_backup()
    {
        var t = new ScriptedEngine(EngineKind.Transcribe, _ => throw new TranscriptionException(TranscriptionErrorKind.InvalidKey, "401", 401));
        var g = new ScriptedEngine(EngineKind.Generate, _ => "nope");
        var service = new TranscriptionService((k, _) => k == EngineKind.Transcribe ? t : g, NullLog.Instance, NoDelay.Delay);

        await Assert.ThrowsAsync<TranscriptionException>(() => service.TranscribeAsync(Req(), new PlainspokenSettings(), null, default));
        Assert.Equal(0, g.Calls);
    }

    private sealed class SyncProgress(List<string> sink) : IProgress<string>
    {
        public void Report(string value) => sink.Add(value);
    }
}
