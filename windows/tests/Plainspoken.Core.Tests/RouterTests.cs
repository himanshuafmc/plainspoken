using System.Diagnostics;
using Plainspoken.Core.Audio;
using Plainspoken.Core.Logging;
using Plainspoken.Core.Settings;
using Plainspoken.Core.Transcription;

namespace Plainspoken.Core.Tests;

public class RouterTests
{
    /// <summary>A scripted engine: waits <see cref="DelayMs"/>, then returns <see cref="Text"/> or throws <see cref="Error"/>.</summary>
    private sealed record Script(string Text = "ok", TranscriptionErrorKind? Error = null, int DelayMs = 0);

    private readonly List<SpeechService> _calls = [];
    private readonly List<TranscriptionRequest> _requests = [];
    private readonly StatsList _stats = new();

    private sealed class StatsList : IStatsRecorder
    {
        public List<DictationStat> Items { get; } = [];

        public void Record(DictationStat stat)
        {
            lock (Items)
            {
                Items.Add(stat);
            }
        }
    }

    private sealed class ScriptedEngine(SpeechService service, Script script, RouterTests owner) : ITranscriptionEngine
    {
        public SpeechService Service => service;

        public async Task<string> TranscribeAsync(TranscriptionRequest request, CancellationToken ct)
        {
            lock (owner._calls)
            {
                owner._calls.Add(service);
                owner._requests.Add(request);
            }

            if (script.DelayMs > 0)
            {
                await Task.Delay(script.DelayMs, ct);
            }

            return script.Error is { } kind ? throw new TranscriptionException(kind, "scripted") : script.Text;
        }
    }

    private static PlainspokenSettings Settings(int hedge, params SpeechService[] order) =>
        new PlainspokenSettings
        {
            Transcription = { ServiceOrder = order.Select(s => s.Id()).ToList(), HedgeSeconds = hedge, Cleanup = { Provider = CleanupProvider.Off } },
        }.Normalize();

    private static PlainspokenSettings Settings(params SpeechService[] order) => Settings(0, order);

    private static TranscriptionRequest Request(double seconds = 2.0, TranscriptionMode mode = TranscriptionMode.Smart) =>
        new(WavEncoder.Encode(TestAudio.Speech(Math.Max(seconds, 0.1))), seconds, mode, LanguagePreset.EnglishHindi, ["WhatsApp"]);

    private TranscriptionService Router(Dictionary<SpeechService, Script> scripts, IEnumerable<SpeechService>? keys = null, ITextCleaner? cleaner = null,
        IAudioCompressor? compressor = null, ServiceHealth? health = null)
    {
        var withKeys = (keys ?? scripts.Keys).ToHashSet();
        return new TranscriptionService(
            (service, _, _) => new ScriptedEngine(service, scripts[service], this),
            withKeys.Contains,
            new ListLog(),
            cleaner is null ? null : _ => cleaner,
            _stats,
            compressor,
            health: health);
    }

    private sealed class SyncProgress(List<string> sink) : IProgress<string>
    {
        public void Report(string value) => sink.Add(value);
    }

    [Fact]
    public async Task The_main_service_is_used_when_it_works()
    {
        var r = Router(new() { [SpeechService.Sarvam] = new("Main."), [SpeechService.Gemini] = new("Backup.") });
        var output = await r.TranscribeAsync(Request(), Settings(SpeechService.Sarvam, SpeechService.Gemini), null, default);
        Assert.Equal("Main.", output.Text);
        Assert.Equal(SpeechService.Sarvam, output.Service);
        Assert.Equal([SpeechService.Sarvam], _calls);
        Assert.False(Assert.Single(_stats.Items).UsedBackup);
    }

    [Fact]
    public async Task A_failure_moves_straight_to_the_next_service()
    {
        var r = Router(new() { [SpeechService.Sarvam] = new(Error: TranscriptionErrorKind.Server), [SpeechService.Deepgram] = new("From Deepgram.") });
        var statuses = new List<string>();
        var output = await r.TranscribeAsync(Request(), Settings(SpeechService.Sarvam, SpeechService.Deepgram), new SyncProgress(statuses), default);
        Assert.Equal("From Deepgram.", output.Text);
        Assert.Equal([SpeechService.Sarvam, SpeechService.Deepgram], _calls);
        Assert.Contains(statuses, s => s.Contains("Deepgram", StringComparison.Ordinal));
        Assert.True(Assert.Single(_stats.Items).UsedBackup);
    }

    [Fact]
    public async Task When_every_service_fails_the_first_error_is_reported_with_its_service()
    {
        var r = Router(new() { [SpeechService.Sarvam] = new(Error: TranscriptionErrorKind.NoCredits), [SpeechService.Gemini] = new(Error: TranscriptionErrorKind.Timeout) });
        var e = await Assert.ThrowsAsync<TranscriptionException>(() => r.TranscribeAsync(Request(), Settings(SpeechService.Sarvam, SpeechService.Gemini), null, default));
        Assert.Equal(TranscriptionErrorKind.NoCredits, e.Kind);
        Assert.Equal(SpeechService.Sarvam, e.Service);
        Assert.False(Assert.Single(_stats.Items).Ok);
    }

    [Fact]
    public async Task A_slow_service_is_hedged_and_the_first_answer_wins()
    {
        var r = Router(new() { [SpeechService.Sarvam] = new("Slow.", DelayMs: 5_000), [SpeechService.Deepgram] = new("Fast.", DelayMs: 50) });
        var sw = Stopwatch.StartNew();
        var output = await r.TranscribeAsync(Request(seconds: 0), Settings(1, SpeechService.Sarvam, SpeechService.Deepgram), null, default);
        Assert.Equal("Fast.", output.Text);
        Assert.True(sw.ElapsedMilliseconds < 3_000, "the slow service should have been cancelled");
        Assert.True(Assert.Single(_stats.Items).Hedged);
    }

    [Fact]
    public void Hedging_is_off_when_the_setting_is_0()
    {
        Assert.Null(TranscriptionService.HedgeDelay(Settings(0, SpeechService.Gemini), 10));
        Assert.Equal(TimeSpan.FromMilliseconds(5_500), TranscriptionService.HedgeDelay(Settings(4, SpeechService.Gemini), 10));
        Assert.Equal(TimeSpan.FromSeconds(20), TranscriptionService.HedgeDelay(Settings(4, SpeechService.Gemini), 600));
    }

    [Fact]
    public async Task Services_without_a_key_are_skipped_and_no_key_at_all_is_an_error()
    {
        var r = Router(new() { [SpeechService.Sarvam] = new("x"), [SpeechService.Gemini] = new("From Gemini.") }, keys: [SpeechService.Gemini]);
        Assert.Equal("From Gemini.", (await r.TranscribeAsync(Request(), Settings(SpeechService.Sarvam, SpeechService.Gemini), null, default)).Text);
        var none = Router(new() { [SpeechService.Sarvam] = new() }, keys: []);
        var e = await Assert.ThrowsAsync<TranscriptionException>(() => none.TranscribeAsync(Request(), Settings(SpeechService.Sarvam), null, default));
        Assert.Equal(TranscriptionErrorKind.NoKey, e.Kind);
        Assert.Equal(SpeechService.Sarvam, e.Service);
    }

    [Fact]
    public async Task A_failing_service_is_paused_and_skipped_while_a_backup_works()
    {
        var clock = new FakeTime(new DateTimeOffset(2026, 10, 18, 10, 0, 0, TimeSpan.Zero));
        var health = new ServiceHealth(failuresBeforePause: 2, pause: TimeSpan.FromMinutes(15), time: clock);
        var r = Router(new() { [SpeechService.Sarvam] = new(Error: TranscriptionErrorKind.Server), [SpeechService.Gemini] = new("G.") }, health: health);
        var s = Settings(SpeechService.Sarvam, SpeechService.Gemini);
        for (var i = 0; i < 2; i++)
        {
            await r.TranscribeAsync(Request(), s, null, default);
        }

        _calls.Clear();
        await r.TranscribeAsync(Request(), s, null, default);
        Assert.Equal([SpeechService.Gemini], _calls); // Sarvam paused
        clock.Now += TimeSpan.FromMinutes(16);
        _calls.Clear();
        await r.TranscribeAsync(Request(), s, null, default);
        Assert.Equal([SpeechService.Sarvam, SpeechService.Gemini], _calls); // pause over: tried again
    }

    [Fact]
    public void Out_of_credits_pauses_at_once()
    {
        var health = new ServiceHealth();
        Assert.True(health.Failure(SpeechService.Sarvam, TranscriptionErrorKind.NoCredits));
        Assert.False(health.IsAvailable(SpeechService.Sarvam));
        health.Reset(SpeechService.Sarvam);
        Assert.True(health.IsAvailable(SpeechService.Sarvam));
        Assert.False(health.Failure(SpeechService.Deepgram, TranscriptionErrorKind.Timeout));
    }

    private sealed class FakeCleaner(Func<string, string> reply) : ITextCleaner
    {
        public List<(string Raw, CleanupRequest Request)> Seen { get; } = [];

        public string Name => "fake";

        public Task<string> CleanAsync(string raw, CleanupRequest request, CancellationToken ct)
        {
            Seen.Add((raw, request));
            return Task.FromResult(reply(raw));
        }
    }

    private sealed class BrokenCleaner : ITextCleaner
    {
        public string Name => "broken";

        public Task<string> CleanAsync(string raw, CleanupRequest request, CancellationToken ct) =>
            throw new TranscriptionException(TranscriptionErrorKind.Server, "503");
    }

    private static PlainspokenSettings WithCleanup(PlainspokenSettings s)
    {
        s.Transcription.Cleanup.Provider = CleanupProvider.Gemini;
        return s;
    }

    [Fact]
    public async Task Raw_text_from_a_non_Gemini_service_is_cleaned_up()
    {
        var cleaner = new FakeCleaner(_ => "So, I think we should go.");
        var r = Router(new() { [SpeechService.Deepgram] = new("um so I I think we should go") }, cleaner: cleaner);
        var s = WithCleanup(Settings(SpeechService.Deepgram));
        s.Snippets = [new Snippet { Trigger = "insert my address", Text = "1 Example Road" }];
        var output = await r.TranscribeAsync(Request(), s, null, default);
        Assert.Equal("So, I think we should go.", output.Text);
        Assert.Equal("fake", output.CleanedBy);
        Assert.Equal(["WhatsApp"], Assert.Single(cleaner.Seen).Request.Vocabulary);
        Assert.Equal(["insert my address"], cleaner.Seen[0].Request.KeepPhrases);
    }

    [Fact]
    public async Task Gemini_text_is_not_cleaned_again_unless_asked()
    {
        var cleaner = new FakeCleaner(_ => "Cleaned.");
        var r = Router(new() { [SpeechService.Gemini] = new("Already tidy text here.") }, cleaner: cleaner);
        Assert.Equal("Already tidy text here.", (await r.TranscribeAsync(Request(), WithCleanup(Settings(SpeechService.Gemini)), null, default)).Text);
        Assert.Empty(cleaner.Seen);
        var again = WithCleanup(Settings(SpeechService.Gemini));
        again.Transcription.Cleanup.AlsoGemini = true;
        Assert.Equal("Cleaned.", (await r.TranscribeAsync(Request(), again, null, default)).Text);
    }

    [Fact]
    public async Task A_refused_or_failed_cleanup_falls_back_to_the_local_filler_filter()
    {
        var summarizer = new FakeCleaner(_ => "Summary.");
        var r = Router(new() { [SpeechService.Sarvam] = new("um so we need the report by Thursday and the sales numbers from finance") }, cleaner: summarizer);
        Assert.Equal("so we need the report by Thursday and the sales numbers from finance",
            (await r.TranscribeAsync(Request(), WithCleanup(Settings(SpeechService.Sarvam)), null, default)).Text);

        var r2 = Router(new() { [SpeechService.Sarvam] = new("uh please send it today") }, cleaner: new BrokenCleaner());
        var output = await r2.TranscribeAsync(Request(), WithCleanup(Settings(SpeechService.Sarvam)), null, default);
        Assert.Equal("please send it today", output.Text);
        Assert.Null(output.CleanedBy);
    }

    [Fact]
    public async Task Verbatim_and_very_short_dictations_skip_the_cleanup()
    {
        var cleaner = new FakeCleaner(_ => "X.");
        var r = Router(new() { [SpeechService.Sarvam] = new("um yes") }, cleaner: cleaner);
        Assert.Equal("yes", (await r.TranscribeAsync(Request(), WithCleanup(Settings(SpeechService.Sarvam)), null, default)).Text);
        var verbatim = WithCleanup(Settings(SpeechService.Sarvam));
        verbatim.Transcription.Mode = TranscriptionMode.Verbatim;
        Assert.Equal("um yes", (await r.TranscribeAsync(Request(mode: TranscriptionMode.Verbatim), verbatim, null, default)).Text);
        Assert.Empty(cleaner.Seen);
    }

    [Fact]
    public async Task Streamed_text_is_finished_the_same_way()
    {
        var cleaner = new FakeCleaner(_ => "Kal meeting hai.");
        var r = Router([], cleaner: cleaner);
        var output = await r.FinishAsync("umm kal meeting hai na", SpeechService.Sarvam, Request(), WithCleanup(Settings(SpeechService.Sarvam)), null, 400, default);
        Assert.Equal("Kal meeting hai.", output.Text);
        var stat = Assert.Single(_stats.Items);
        Assert.True(stat.Streamed);
        Assert.Equal(400, stat.ListenMs);
    }

    private sealed class FakeCompressor : IAudioCompressor
    {
        public EncodedAudio? Compress(short[] samples, int sampleRate) => new(new byte[10], "audio/mp4", "audio.m4a");
    }

    [Fact]
    public async Task Compressed_audio_goes_only_to_services_that_accept_it()
    {
        var r = Router(new() { [SpeechService.Sarvam] = new("S.") }, compressor: new FakeCompressor());
        await r.TranscribeAsync(Request(), Settings(SpeechService.Sarvam), null, default);
        var sent = Assert.Single(_requests);
        Assert.NotNull(sent.Compressed);
        Assert.Equal("audio.m4a", sent.AudioFor(SpeechService.Sarvam).FileName);
        Assert.Equal("audio.wav", sent.AudioFor(SpeechService.Gemini).FileName);

        _requests.Clear();
        var off = Settings(SpeechService.Sarvam);
        off.Transcription.CompressAudio = false;
        await r.TranscribeAsync(Request(), off, null, default);
        Assert.Null(Assert.Single(_requests).Compressed);

        _requests.Clear();
        await Router(new() { [SpeechService.Gemini] = new("G.") }, compressor: new FakeCompressor()).TranscribeAsync(Request(), Settings(SpeechService.Gemini), null, default);
        Assert.Null(Assert.Single(_requests).Compressed); // Gemini alone: not worth compressing
    }

    [Fact]
    public void Snippets_expand_as_a_whole_dictation_or_inside_a_sentence()
    {
        List<Snippet> snippets =
        [
            new() { Trigger = "insert my address", Text = "1 Example Road, Pune" },
            new() { Trigger = "my email sign off", Text = "Best regards" },
        ];
        Assert.Equal("1 Example Road, Pune", SnippetExpander.Expand("Insert my address.", snippets));
        Assert.Equal("Please deliver to 1 Example Road, Pune tomorrow.", SnippetExpander.Expand("Please deliver to insert my address tomorrow.", snippets));
        Assert.Equal("Thanks. Best regards", SnippetExpander.Expand("Thanks. My email, sign off", snippets));
        Assert.Equal("My address is new.", SnippetExpander.Expand("My address is new.", snippets));
        Assert.Equal("Costs $5", SnippetExpander.Expand("Costs insert my address", [new() { Trigger = "insert my address", Text = "$5" }]));
    }

    [Fact]
    public async Task Warm_up_asks_the_first_usable_service_once_per_interval_and_never_throws()
    {
        var calls = new List<SpeechService>();
        var service = new TranscriptionService((s, _, _) => throw new InvalidOperationException(), s => s == SpeechService.Deepgram, NullLog.Instance,
            warmUp: (s, _, _) =>
            {
                lock (calls)
                {
                    calls.Add(s);
                }

                throw new HttpRequestException("offline");
            });

        var settings = Settings(SpeechService.Sarvam, SpeechService.Deepgram);
        service.WarmUp(settings);
        service.WarmUp(settings); // within 20 s: ignored
        await Task.Delay(200);
        Assert.Equal([SpeechService.Deepgram], calls);
    }

    [Fact]
    public async Task Gemini_warm_up_is_a_metadata_get_and_others_just_open_the_connection()
    {
        var fake = new GeminiFake();
        var warmUp = TranscriptionService.StandardWarmUp(fake.Client(), _ => "k", NullLog.Instance);
        await warmUp(SpeechService.Gemini, new PlainspokenSettings().Normalize(), default);
        var call = Assert.Single(fake.Handler.Requests);
        Assert.Equal(HttpMethod.Get, call.Method);
        Assert.Equal("https://generativelanguage.googleapis.com/v1beta/models/gemini-3.5-transcribe", call.Uri.AbsoluteUri);

        var other = new FakeHttpHandler((_, _) => FakeHttpHandler.Json(System.Net.HttpStatusCode.NotFound, "{}"));
        await TranscriptionService.StandardWarmUp(new HttpClient(other), _ => "k", NullLog.Instance)(SpeechService.Sarvam, new PlainspokenSettings().Normalize(), default);
        var get = Assert.Single(other.Requests);
        Assert.Equal("https://api.sarvam.ai/", get.Uri.AbsoluteUri);
        Assert.False(get.Headers.ContainsKey("api-subscription-key")); // no key needed to open a connection
    }

    [Fact]
    public async Task The_real_engine_factory_builds_every_service()
    {
        var http = new HttpClient(new FakeHttpHandler((_, _) => FakeHttpHandler.Json(System.Net.HttpStatusCode.OK, "{}")));
        var factory = TranscriptionService.Engines(http, _ => "k", NullLog.Instance);
        var settings = new PlainspokenSettings().Normalize();
        foreach (var s in SpeechServices.All)
        {
            Assert.Equal(s, factory(s, settings, 0).Service);
        }

        await Task.CompletedTask;
    }
}
