using Plainspoken.Core.Logging;
using Plainspoken.Core.Settings;

namespace Plainspoken.Core.Transcription;

public sealed record TranscriptionOutcome(string Text, EngineKind Engine);

/// <summary>Transcribes a recording using the configured engine(s). Used by the dictation controller.</summary>
public interface ITranscriber
{
    Task<TranscriptionOutcome> TranscribeAsync(TranscriptionRequest request, PlainspokenSettings settings,
        IProgress<string>? status, CancellationToken ct);

    /// <summary>
    /// Opens the HTTPS connection in the background (called when recording starts) so the real request
    /// doesn't pay for DNS/TCP/TLS setup. Never throws; does nothing if called again within 20 s.
    /// </summary>
    void WarmUp(PlainspokenSettings settings);
}

/// <summary>
/// Chooses the engine, and on a 429 either tries the backup engine once (if enabled) or,
/// for short per-minute limits, waits and retries once. 5xx retries happen inside the engines.
/// </summary>
public sealed class TranscriptionService : ITranscriber
{
    /// <summary>Longest per-minute wait we do automatically instead of failing.</summary>
    public static readonly TimeSpan MaxAutoWait = TimeSpan.FromSeconds(30);

    private readonly Func<EngineKind, PlainspokenSettings, ITranscriptionEngine> _engineFactory;
    private readonly ILog _log;
    private static readonly TimeSpan WarmUpInterval = TimeSpan.FromSeconds(20);

    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Func<PlainspokenSettings, CancellationToken, Task>? _warmUp;
    private long _lastWarmUpTicks;

    public TranscriptionService(Func<EngineKind, PlainspokenSettings, ITranscriptionEngine> engineFactory, ILog log,
        Func<TimeSpan, CancellationToken, Task>? delay = null, Func<PlainspokenSettings, CancellationToken, Task>? warmUp = null)
    {
        _engineFactory = engineFactory ?? throw new ArgumentNullException(nameof(engineFactory));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _delay = delay ?? Task.Delay;
        _warmUp = warmUp;
    }

    /// <summary>Warm-up for the real Gemini API: a tiny GET of the model's metadata (costs no generation quota).</summary>
    public static Func<PlainspokenSettings, CancellationToken, Task> GeminiWarmUp(HttpClient http, Func<string?> apiKey, ILog log) =>
        async (settings, ct) =>
        {
            var t = settings.Transcription;
            var client = new GeminiClient(http, t.ApiBaseUrl, apiKey, log, new RetryPolicy(maxServerRetries: 0));
            var model = ModelId.Normalize(t.ModelFor(t.Engine));
            using var doc = await client.SendJsonOnceAsync(HttpMethod.Get, "v1beta/models/" + Uri.EscapeDataString(model), null,
                TimeSpan.FromSeconds(10), "warm-up", true, ct).ConfigureAwait(false);
        };

    public void WarmUp(PlainspokenSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (_warmUp is null)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow.Ticks;
        var last = Interlocked.Read(ref _lastWarmUpTicks);
        if (now - last < WarmUpInterval.Ticks || Interlocked.CompareExchange(ref _lastWarmUpTicks, now, last) != last)
        {
            return;
        }

        var warmUp = _warmUp;
        _ = Task.Run(async () =>
        {
            try
            {
                await warmUp(settings, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.Info($"warm-up skipped: {ex.GetType().Name}");
            }
        });
    }

    /// <summary>Factory for the real Gemini engines.</summary>
    public static Func<EngineKind, PlainspokenSettings, ITranscriptionEngine> GeminiEngines(HttpClient http, Func<string?> apiKey,
        ILog log, RetryPolicy? retry = null, bool requireApiKey = true) =>
        (kind, settings) =>
        {
            var t = settings.Transcription;
            var client = new GeminiClient(http, t.ApiBaseUrl, apiKey, log, retry, requireApiKey);
            var timeout = TimeSpan.FromSeconds(t.RequestTimeoutSeconds);
            return kind == EngineKind.Transcribe
                ? new GeminiTranscribeEngine(client, t.TranscribeModel, timeout)
                : new GeminiGenerateEngine(client, t.GenerateModel, timeout);
        };

    public async Task<TranscriptionOutcome> TranscribeAsync(TranscriptionRequest request, PlainspokenSettings settings,
        IProgress<string>? status, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(settings);
        var primary = settings.Transcription.Engine;
        var backup = primary == EngineKind.Transcribe ? EngineKind.Generate : EngineKind.Transcribe;
        _log.Info($"transcribe: engine={primary} wav={request.Wav.Length} bytes duration={request.DurationSeconds:0.0}s " +
                  $"mode={request.Mode} languages={request.Languages} vocabulary={request.Vocabulary.Count}");

        try
        {
            return await RunAsync(primary, request, settings, ct).ConfigureAwait(false);
        }
        catch (TranscriptionException first) when (first.IsRateLimit)
        {
            if (settings.Transcription.UseBackupEngineWhenLimited)
            {
                status?.Report("Busy — trying backup engine…");
                try
                {
                    var outcome = await RunAsync(backup, request, settings, ct).ConfigureAwait(false);
                    _log.Info($"transcribe: {primary} was rate-limited; {backup} succeeded");
                    return outcome;
                }
                catch (TranscriptionException second)
                {
                    _log.Warn($"transcribe: backup {backup} also failed: {second.Kind}");
                }
            }

            if (first.Kind == TranscriptionErrorKind.RateLimited && first.RetryAfter is { } wait && wait <= MaxAutoWait)
            {
                status?.Report($"Free limit busy — retrying in {Math.Ceiling(wait.TotalSeconds):0} s…");
                await _delay(wait + TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
                return await RunAsync(primary, request, settings, ct).ConfigureAwait(false);
            }

            throw;
        }
    }

    private async Task<TranscriptionOutcome> RunAsync(EngineKind kind, TranscriptionRequest request, PlainspokenSettings settings, CancellationToken ct)
    {
        var engine = _engineFactory(kind, settings);
        var started = DateTimeOffset.UtcNow;
        var text = await engine.TranscribeAsync(request, ct).ConfigureAwait(false);
        _log.Info($"transcribe: {kind} ok in {(DateTimeOffset.UtcNow - started).TotalMilliseconds:0} ms, {text.Length} chars");
        return new TranscriptionOutcome(text, kind);
    }
}
