using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Plainspoken.Core.Audio;
using Plainspoken.Core.Logging;
using Plainspoken.Core.Settings;

namespace Plainspoken.Core.Transcription;

/// <summary>The finished text, which service heard it, and which clean-up service tidied it (null = not cleaned).</summary>
public sealed record TranscriptionOutcome(string Text, SpeechService Service, string? CleanedBy = null);

/// <summary>Turns a recording into finished text: the speech service(s), then clean-up and snippets. Used by the dictation controller.</summary>
public interface ITranscriber
{
    Task<TranscriptionOutcome> TranscribeAsync(TranscriptionRequest request, PlainspokenSettings settings, IProgress<string>? status, CancellationToken ct);

    /// <summary>For text that already came from a live stream: clean-up and snippets only.</summary>
    Task<TranscriptionOutcome> FinishAsync(string raw, SpeechService service, TranscriptionRequest request, PlainspokenSettings settings,
        IProgress<string>? status, long listenMs, CancellationToken ct);

    /// <summary>
    /// Opens the HTTPS connection in the background (called when recording starts) so the real request
    /// doesn't pay for DNS/TCP/TLS setup. Never throws; does nothing if called again within 20 s.
    /// </summary>
    void WarmUp(PlainspokenSettings settings);
}

/// <summary>One dictation, for the stats window. Never holds any text.</summary>
/// <param name="UsedBackup">A backup service produced the text.</param>
/// <param name="Hedged">A second service was asked because the first was slow.</param>
public sealed record DictationStat(
    DateTimeOffset Utc,
    SpeechService Service,
    double AudioSeconds,
    long ListenMs,
    long CleanupMs,
    bool Ok,
    string? ErrorKind = null,
    bool Streamed = false,
    bool UsedBackup = false,
    bool Hedged = false,
    string? CleanedBy = null);

public interface IStatsRecorder
{
    void Record(DictationStat stat);
}

/// <summary>
/// Remembers which services are failing. After <c>failuresBeforePause</c> failures in a row (at once for "out of credits"
/// and "daily limit"), a service is skipped for <c>pause</c> while another one is available.
/// </summary>
public sealed class ServiceHealth(int failuresBeforePause = 2, TimeSpan? pause = null, TimeProvider? time = null)
{
    private readonly TimeSpan _pause = pause ?? TimeSpan.FromMinutes(15);
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly ConcurrentDictionary<SpeechService, int> _failures = new();
    private readonly ConcurrentDictionary<SpeechService, DateTimeOffset> _pausedUntil = new();

    public bool IsAvailable(SpeechService service) => !_pausedUntil.TryGetValue(service, out var until) || _time.GetUtcNow() >= until;

    public void Success(SpeechService service)
    {
        _failures.TryRemove(service, out _);
        _pausedUntil.TryRemove(service, out _);
    }

    /// <summary>Returns true if this failure paused the service.</summary>
    public bool Failure(SpeechService service, TranscriptionErrorKind kind)
    {
        var immediate = kind is TranscriptionErrorKind.NoCredits or TranscriptionErrorKind.DailyQuota;
        var count = _failures.AddOrUpdate(service, 1, (_, n) => n + 1);
        if (!immediate && count < failuresBeforePause)
        {
            return false;
        }

        _failures.TryRemove(service, out _);
        _pausedUntil[service] = _time.GetUtcNow() + _pause;
        return true;
    }

    /// <summary>Forget everything about <paramref name="service"/> (for example after its key was changed).</summary>
    public void Reset(SpeechService service) => Success(service);
}

/// <summary>
/// The router. Uses the services in the user's order (main first) that have a key, skipping ones <see cref="Health"/> has
/// paused. If a service fails, the next is tried at once; if it is slow (see <see cref="HedgeDelay"/>), the next one is
/// asked as well and the first answer wins. Then the clean-up step tidies raw text (Smart mode), with a local filler filter
/// as the fallback, and snippets are expanded.
/// </summary>
public sealed class TranscriptionService : ITranscriber
{
    /// <summary>Very short dictations ("yes", "on my way") don't need the clean-up round trip.</summary>
    public const int MinWordsToClean = 3;

    private static readonly TimeSpan WarmUpInterval = TimeSpan.FromSeconds(20);

    private readonly Func<SpeechService, PlainspokenSettings, int, ITranscriptionEngine> _engineFactory;
    private readonly Func<SpeechService, bool> _hasKey;
    private readonly ILog _log;
    private readonly Func<PlainspokenSettings, ITextCleaner?>? _cleaners;
    private readonly IStatsRecorder? _stats;
    private readonly IAudioCompressor? _compressor;
    private readonly Func<SpeechService, PlainspokenSettings, CancellationToken, Task>? _warmUp;
    private readonly TimeProvider _time;
    private long _lastWarmUpTicks;

    /// <param name="engineFactory">(service, settings, server retries) → engine.</param>
    public TranscriptionService(
        Func<SpeechService, PlainspokenSettings, int, ITranscriptionEngine> engineFactory,
        Func<SpeechService, bool> hasKey,
        ILog log,
        Func<PlainspokenSettings, ITextCleaner?>? cleaners = null,
        IStatsRecorder? stats = null,
        IAudioCompressor? compressor = null,
        Func<SpeechService, PlainspokenSettings, CancellationToken, Task>? warmUp = null,
        TimeProvider? time = null,
        ServiceHealth? health = null)
    {
        _engineFactory = engineFactory ?? throw new ArgumentNullException(nameof(engineFactory));
        _hasKey = hasKey ?? throw new ArgumentNullException(nameof(hasKey));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _cleaners = cleaners;
        _stats = stats;
        _compressor = compressor;
        _warmUp = warmUp;
        _time = time ?? TimeProvider.System;
        Health = health ?? new ServiceHealth(time: _time);
    }

    public ServiceHealth Health { get; }

    public void WarmUp(PlainspokenSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (_warmUp is not { } warmUp || UsableServices(settings) is not [var service, ..])
        {
            return;
        }

        var now = DateTimeOffset.UtcNow.Ticks;
        var last = Interlocked.Read(ref _lastWarmUpTicks);
        if (now - last < WarmUpInterval.Ticks || Interlocked.CompareExchange(ref _lastWarmUpTicks, now, last) != last)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await warmUp(service, settings, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.Info($"warm-up skipped: {ex.GetType().Name}");
            }
        });
    }

    /// <summary>Services in the user's order that have a key, without paused ones (unless all are paused).</summary>
    public IReadOnlyList<SpeechService> UsableServices(PlainspokenSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var withKeys = settings.Transcription.OrderedServices().Where(_hasKey).ToList();
        var available = withKeys.Where(Health.IsAvailable).ToList();
        return available.Count > 0 ? available : withKeys;
    }

    public async Task<TranscriptionOutcome> TranscribeAsync(TranscriptionRequest request, PlainspokenSettings settings, IProgress<string>? status,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(settings);
        var order = UsableServices(settings);
        if (order.Count == 0)
        {
            throw new TranscriptionException(TranscriptionErrorKind.NoKey, "No key for any chosen service.",
                service: settings.Transcription.OrderedServices()[0]);
        }

        _log.Info(string.Create(CultureInfo.InvariantCulture,
            $"transcribe: services={string.Join(",", order.Select(s => s.Id()))} wav={request.Wav.Length} bytes duration={request.DurationSeconds:0.0}s " +
            $"mode={request.Mode} languages={request.Languages} vocabulary={request.Vocabulary.Count}"));

        var prepared = await CompressIfUsefulAsync(request, settings, order, ct).ConfigureAwait(false);
        var sw = Stopwatch.StartNew();
        Heard heard;
        try
        {
            heard = await ListenAsync(prepared, settings, order, status, ct).ConfigureAwait(false);
        }
        catch (TranscriptionException ex)
        {
            _stats?.Record(new DictationStat(_time.GetUtcNow(), ex.Service ?? order[0], request.DurationSeconds, sw.ElapsedMilliseconds, 0, Ok: false,
                ErrorKind: ex.Kind.ToString()));
            throw;
        }

        return await FinishTextAsync(heard.Text, heard.Service, request, settings, status, sw.ElapsedMilliseconds, streamed: false,
            usedBackup: heard.Service != order[0], hedged: heard.Hedged, ct).ConfigureAwait(false);
    }

    public Task<TranscriptionOutcome> FinishAsync(string raw, SpeechService service, TranscriptionRequest request, PlainspokenSettings settings,
        IProgress<string>? status, long listenMs, CancellationToken ct) =>
        FinishTextAsync(raw, service, request, settings, status, listenMs, streamed: true, usedBackup: false, hedged: false, ct);

    private sealed record Heard(string Text, SpeechService Service, bool Hedged);

    private sealed record Attempt(SpeechService Service, string? Text, TranscriptionException? Error);

    /// <summary>Runs the services in order with fallback and hedging; returns the first transcript.</summary>
    private async Task<Heard> ListenAsync(TranscriptionRequest request, PlainspokenSettings settings, IReadOnlyList<SpeechService> order,
        IProgress<string>? status, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var hedge = HedgeDelay(settings, request.DurationSeconds);
        var serverRetries = order.Count > 1 ? 0 : 2; // with a backup, a busy service is skipped rather than retried
        var running = new List<Task<Attempt>>();
        var errors = new List<TranscriptionException>();
        var next = 0;
        var hedged = false;

        void LaunchNext()
        {
            var service = order[next++];
            running.Add(Task.Run(() => RunOneAsync(service, request, settings, serverRetries, cts.Token), cts.Token));
        }

        try
        {
            LaunchNext();
            while (true)
            {
                Task finished;
                if (next < order.Count && hedge is { } wait)
                {
                    using var timerCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
                    var timer = Task.Delay(wait, _time, timerCts.Token);
                    finished = await Task.WhenAny(running.Append<Task>(timer)).ConfigureAwait(false);
                    timerCts.Cancel();
                    ct.ThrowIfCancellationRequested();
                    if (finished == timer)
                    {
                        _log.Info($"transcribe: {order[next - 1].Id()} is slow; also asking {order[next].Id()}");
                        status?.Report($"Still working — also asking {order[next].ShortName()}…");
                        hedged = true;
                        LaunchNext();
                        continue;
                    }
                }
                else
                {
                    finished = await Task.WhenAny(running).ConfigureAwait(false);
                }

                var task = (Task<Attempt>)finished;
                running.Remove(task);
                var attempt = await task.ConfigureAwait(false);
                if (attempt.Error is null)
                {
                    Health.Success(attempt.Service);
                    return new Heard(attempt.Text ?? string.Empty, attempt.Service, hedged);
                }

                errors.Add(attempt.Error);
                if (Health.Failure(attempt.Service, attempt.Error.Kind))
                {
                    _log.Warn($"transcribe: {attempt.Service.Id()} paused for 15 min after repeated failures");
                }

                if (next < order.Count)
                {
                    if (running.Count == 0)
                    {
                        status?.Report($"Trying {order[next].ShortName()}…");
                    }

                    if (running.Count == 0 || hedge is null)
                    {
                        LaunchNext();
                    }
                }
                else if (running.Count == 0)
                {
                    throw errors[0];
                }
            }
        }
        finally
        {
            // Cancel the slower services; their results are no longer needed.
            await cts.CancelAsync().ConfigureAwait(false);
            foreach (var loser in running)
            {
                _ = loser.ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            }
        }
    }

    private async Task<Attempt> RunOneAsync(SpeechService service, TranscriptionRequest request, PlainspokenSettings settings, int serverRetries,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var text = await _engineFactory(service, settings, serverRetries).TranscribeAsync(request, ct).ConfigureAwait(false);
            _log.Info($"transcribe: {service.Id()} ok in {sw.ElapsedMilliseconds} ms, {text.Length} chars");
            return new Attempt(service, text, null);
        }
        catch (TranscriptionException ex)
        {
            _log.Warn($"transcribe: {service.Id()} failed after {sw.ElapsedMilliseconds} ms: {ex.Kind}");
            return new Attempt(service, null, ex.WithService(service));
        }
    }

    private async Task<TranscriptionOutcome> FinishTextAsync(string raw, SpeechService service, TranscriptionRequest request,
        PlainspokenSettings settings, IProgress<string>? status, long listenMs, bool streamed, bool usedBackup, bool hedged, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(raw);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(settings);
        var sw = Stopwatch.StartNew();
        var text = raw.Trim();
        string? cleanedBy = null;
        if (ShouldClean(settings, service, text) && _cleaners?.Invoke(settings) is { } cleaner)
        {
            status?.Report("Tidying up…");
            try
            {
                var cleanup = new CleanupRequest(request.Vocabulary, request.HindiScript, settings.Snippets.Select(s => s.Trigger).ToList());
                var output = await cleaner.CleanAsync(text, cleanup, ct).ConfigureAwait(false);
                if (CleanupGuard.Accept(text, output) is { } accepted)
                {
                    text = accepted;
                    cleanedBy = cleaner.Name;
                }
                else
                {
                    _log.Warn("cleanup: result refused by the safety check; using the service's text");
                }
            }
            catch (TranscriptionException ex)
            {
                _log.Warn($"cleanup: {cleaner.Name} failed ({ex.Kind}); using the service's text");
            }
        }

        if (cleanedBy is null && request.Mode == TranscriptionMode.Smart)
        {
            text = LocalFillerRemover.Clean(text);
        }

        text = SnippetExpander.Expand(text, settings.Snippets);
        var cleanupMs = sw.ElapsedMilliseconds;
        if (cleanedBy is not null)
        {
            _log.Info($"cleanup: {cleanedBy} in {cleanupMs} ms");
        }

        _stats?.Record(new DictationStat(_time.GetUtcNow(), service, request.DurationSeconds, listenMs, cleanupMs, Ok: true, Streamed: streamed,
            UsedBackup: usedBackup, Hedged: hedged, CleanedBy: cleanedBy));
        return new TranscriptionOutcome(text, service, cleanedBy);
    }

    private static bool ShouldClean(PlainspokenSettings settings, SpeechService service, string raw)
    {
        var c = settings.Transcription.Cleanup;
        return settings.Transcription.Mode == TranscriptionMode.Smart && c.Provider != CleanupProvider.Off &&
               (!service.Info().CleansItself || c.AlsoGemini) && CleanupGuard.Words(raw) >= MinWordsToClean;
    }

    /// <summary>Compresses once, before any service is asked, if one of them accepts it. Failures just mean WAV.</summary>
    private async Task<TranscriptionRequest> CompressIfUsefulAsync(TranscriptionRequest request, PlainspokenSettings settings,
        IReadOnlyList<SpeechService> order, CancellationToken ct)
    {
        if (_compressor is not { } compressor || !settings.Transcription.CompressAudio || !order.Any(s => s.Info().AcceptsCompressedAudio))
        {
            return request;
        }

        var sw = Stopwatch.StartNew();
        EncodedAudio? encoded;
        try
        {
            encoded = await Task.Run(() =>
            {
                var (samples, rate) = WavEncoder.Decode(request.Wav);
                return compressor.Compress(samples, rate);
            }, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Warn($"compress: failed ({ex.GetType().Name}); sending WAV");
            return request;
        }

        if (encoded is null || encoded.Bytes.Length >= request.Wav.Length)
        {
            return request;
        }

        _log.Info($"compress: {request.Wav.Length} → {encoded.Bytes.Length} bytes in {sw.ElapsedMilliseconds} ms");
        return request with { Compressed = encoded };
    }

    /// <summary>When to also ask the next service: the user's seconds plus a bit for long recordings, at most 20 s. Null = never.</summary>
    public static TimeSpan? HedgeDelay(PlainspokenSettings settings, double durationSeconds)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var baseSeconds = settings.Transcription.HedgeSeconds;
        return baseSeconds <= 0 ? null : TimeSpan.FromMilliseconds((long)(Math.Min(baseSeconds + (durationSeconds * 0.15), 20.0) * 1000));
    }

    /// <summary>Factory for the real engines.</summary>
    public static Func<SpeechService, PlainspokenSettings, int, ITranscriptionEngine> Engines(HttpClient http, Func<SpeechService, string?> keyFor,
        ILog log, bool requireGeminiKey = true) =>
        (service, settings, serverRetries) =>
        {
            var t = settings.Transcription;
            var timeout = TimeSpan.FromSeconds(t.RequestTimeoutSeconds);
            var retry = serverRetries == RetryPolicy.Default.MaxServerRetries ? RetryPolicy.Default : new RetryPolicy(maxServerRetries: serverRetries);
            ServiceClient Client() => new(http, service, t.BaseUrlOf(service), () => keyFor(service), log, retry);
            GeminiClient Gemini() => new(http, t.ApiBaseUrl, () => keyFor(service), log, retry, requireGeminiKey);
            var config = t.Services[service];
            return service switch
            {
                SpeechService.Gemini => new GeminiTranscribeEngine(Gemini(), t.TranscribeModel, timeout),
                SpeechService.GeminiGeneral => new GeminiGenerateEngine(Gemini(), t.GenerateModel, timeout),
                SpeechService.Sarvam => new SarvamEngine(Client(), config, timeout),
                SpeechService.OpenAI => new OpenAIEngine(Client(), config, timeout),
                SpeechService.Deepgram => new DeepgramEngine(Client(), config, timeout),
                SpeechService.ElevenLabs => new ElevenLabsEngine(Client(), config, timeout),
                _ => throw new ArgumentOutOfRangeException(nameof(service)),
            };
        };

    /// <summary>
    /// Warm-up: Gemini gets a tiny GET of the model (costs no quota); the others an unauthenticated GET of the
    /// API host, which only opens the connection for the real request.
    /// </summary>
    public static Func<SpeechService, PlainspokenSettings, CancellationToken, Task> StandardWarmUp(HttpClient http, Func<SpeechService, string?> keyFor,
        ILog log) =>
        async (service, settings, ct) =>
        {
            var t = settings.Transcription;
            if (service.IsGemini())
            {
                var client = new GeminiClient(http, t.ApiBaseUrl, () => keyFor(service), log, new RetryPolicy(maxServerRetries: 0));
                using var doc = await client.SendJsonOnceAsync(HttpMethod.Get, "v1beta/models/" + Uri.EscapeDataString(ModelId.Normalize(t.ModelOf(service))),
                    null, TimeSpan.FromSeconds(10), "warm-up", true, ct).ConfigureAwait(false);
            }
            else
            {
                var baseUrl = PlainspokenSettings.NormalizeBaseUrl(t.BaseUrlOf(service), service.Info().DefaultBaseUrl);
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(10));
                using var response = await http.GetAsync(new Uri(baseUrl + "/"), cts.Token).ConfigureAwait(false);
                log.Info($"warm-up: {service.Id()} HTTP {(int)response.StatusCode}");
            }
        };
}

/// <summary>Spoken shortcuts: a dictation that is only a trigger becomes its text; a trigger inside a sentence is replaced.</summary>
public static partial class SnippetExpander
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(1);

    public static string Expand(string text, IReadOnlyList<Snippet> snippets)
    {
        ArgumentNullException.ThrowIfNull(snippets);
        if (snippets.Count == 0 || string.IsNullOrWhiteSpace(text))
        {
            return text ?? string.Empty;
        }

        var whole = Normalize(text);
        if (snippets.FirstOrDefault(s => Normalize(s.Trigger) == whole) is { } exact)
        {
            return exact.Text;
        }

        var output = text;
        foreach (var s in snippets.OrderByDescending(s => s.Trigger.Length))
        {
            var words = Normalize(s.Trigger).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0)
            {
                continue;
            }

            var pattern = @"(?<![\p{L}\p{M}\p{N}])" + string.Join(@"[\s,]+", words.Select(Regex.Escape)) + @"(?![\p{L}\p{M}\p{N}])";
            output = Regex.Replace(output, pattern, _ => s.Text, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, MatchTimeout);
        }

        return output;
    }

    private static string Normalize(string s) => Spaces().Replace(Punctuation().Replace(s.ToLowerInvariant(), " "), " ").Trim();

    [GeneratedRegex(@"[\p{P}\p{S}]")]
    private static partial Regex Punctuation();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
