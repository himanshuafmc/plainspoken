using System.Buffers.Binary;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Plainspoken.Core.Logging;
using Plainspoken.Core.Settings;

namespace Plainspoken.Core.Transcription;

/// <summary>How one service streams: where to connect, how audio and the end are sent, and how replies are read.</summary>
public interface IStreamProtocol
{
    SpeechService Service { get; }

    /// <summary>Sent after the last audio, asking the service to finish.</summary>
    string EndMessage { get; }

    /// <summary>Sent (best effort) before closing, if the service wants one.</summary>
    string? CloseMessage { get; }

    Uri Uri(PlainspokenSettings settings);

    Task SendAudioAsync(ILiveSocket socket, byte[] pcm, CancellationToken ct);

    /// <summary>
    /// Reads one server message, adding finished text to <paramref name="pieces"/> and what it says about speech to
    /// <paramref name="progress"/>. Returns true when the service has confirmed the end of the stream. Throws
    /// <see cref="TranscriptionException"/> for an error message.
    /// </summary>
    bool Apply(string message, List<string> pieces, bool endSent, StreamProgress progress);
}

/// <summary>
/// What a service that reports when speech starts and ends (Sarvam's VAD signals) has said in one dictation, so the end
/// can be told apart from a slow reply. Each stretch of speech gets a transcript once it ends, or once the end of audio
/// ends it.
/// </summary>
public sealed class StreamProgress
{
    private bool _reported;
    private bool _open;
    private int _ended; // stretches that ended and haven't been transcribed yet

    /// <summary>Transcripts still to come, or null when the service doesn't say.</summary>
    public int? Owed => _reported ? _ended + (_open ? 1 : 0) : null;

    public void SpeechStarted()
    {
        _reported = true;
        _open = true;
    }

    public void SpeechEnded()
    {
        _reported = true;
        if (!_open)
        {
            return;
        }

        _open = false;
        _ended++;
    }

    /// <summary>A transcript arrived. Before the end of audio, one for a stretch still going on is only part of it.</summary>
    public void Transcribed(bool endSent)
    {
        if (_ended > 0)
        {
            _ended--;
        }
        else if (endSent)
        {
            _open = false;
        }
    }
}

/// <summary>
/// Streaming for Sarvam and Deepgram (Gemini has its own, <see cref="GeminiLiveTranscriber"/>). Audio pushed before the
/// socket is open is buffered. After <see cref="FailuresBeforePause"/> failed dictations in a row streaming pauses for
/// <see cref="PauseAfterFailures"/> and the normal request is used, so a network that blocks WebSockets doesn't add a
/// delay every time.
/// </summary>
public sealed class StreamingTranscriber(IStreamProtocol protocol, Func<ILiveSocket> socketFactory, Func<SpeechService, string?> keyFor, ILog log)
    : ILiveTranscriber
{
    private readonly Lock _gate = new();
    private int _failures;
    private DateTimeOffset _pausedUntil = DateTimeOffset.MinValue;

    public TimeSpan StepTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>After the end of audio, stop once nothing more arrives for this long (services that don't say what's to come).</summary>
    public TimeSpan QuietAfterEnd { get; init; } = TimeSpan.FromMilliseconds(900);

    /// <summary>After the end of audio with no transcript owed, stop once nothing more arrives for this long.</summary>
    public TimeSpan SettleAfterEnd { get; init; } = TimeSpan.FromMilliseconds(300);

    /// <summary>After the end of audio, wait this long for each transcript the service still owes.</summary>
    public TimeSpan OwedAfterEnd { get; init; } = TimeSpan.FromMilliseconds(2500);

    public int FailuresBeforePause { get; init; } = 2;

    public TimeSpan PauseAfterFailures { get; init; } = TimeSpan.FromMinutes(15);

    public TimeProvider Clock { get; init; } = TimeProvider.System;

    internal IStreamProtocol Protocol => protocol;

    internal ILog Log => log;

    internal string? Key => keyFor(protocol.Service)?.Trim() is { Length: > 0 } key ? key : null;

    internal ILiveSocket NewSocket() => socketFactory();

    public bool CanStart(PlainspokenSettings settings)
    {
        if (Key is null)
        {
            return false;
        }

        lock (_gate)
        {
            return Clock.GetUtcNow() >= _pausedUntil;
        }
    }

    public ILiveSession Start(PlainspokenSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new StreamingSession(this, settings);
    }

    /// <summary>Records how a finished dictation went. Cancelled dictations are not reported.</summary>
    internal void Report(bool success)
    {
        lock (_gate)
        {
            if (success)
            {
                _failures = 0;
                return;
            }

            if (++_failures < FailuresBeforePause)
            {
                return;
            }

            _failures = 0;
            _pausedUntil = Clock.GetUtcNow() + PauseAfterFailures;
        }

        log.Warn($"live {protocol.Service.Id()}: {FailuresBeforePause} failures in a row; paused for {PauseAfterFailures.TotalMinutes:0} min");
    }
}

internal sealed class StreamingSession : ILiveSession
{
    private const int ChunkSamples = 1600; // 100 ms at 16 kHz

    private readonly StreamingTranscriber _owner;
    private readonly PlainspokenSettings _settings;
    private readonly Channel<short[]> _audio = Channel.CreateUnbounded<short[]>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _abort = new();
    private readonly Task<string> _run;
    private double _pushedSeconds;

    public StreamingSession(StreamingTranscriber owner, PlainspokenSettings settings)
    {
        _owner = owner;
        _settings = settings;
        _run = Task.Run(() => RunAsync(_abort.Token));
        _ = _run.ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
    }

    public SpeechService Service => _owner.Protocol.Service;

    private string Name => Service.Id();

    private ILog Log => _owner.Log;

    public void Push(short[] samples)
    {
        if (samples is { Length: > 0 } && _audio.Writer.TryWrite(samples))
        {
            _pushedSeconds += samples.Length / 16000.0;
        }
    }

    public async Task<string> FinishAsync(CancellationToken ct)
    {
        _audio.Writer.TryComplete();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Min(30, 8 + (_pushedSeconds / 10))));
        try
        {
            var text = await _run.WaitAsync(timeout.Token).ConfigureAwait(false);
            _owner.Report(success: !string.IsNullOrWhiteSpace(text));
            return text;
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested && !_abort.IsCancellationRequested)
        {
            Abort();
            _owner.Report(success: false);
            throw new TranscriptionException(TranscriptionErrorKind.Timeout, $"live {Name}: no final transcript in time", inner: ex, service: Service);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            _owner.Report(success: false);
            throw;
        }
    }

    public void Abort()
    {
        _audio.Writer.TryComplete();
        try
        {
            _abort.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public void Dispose()
    {
        Abort();
        _ = _run.ContinueWith(_ => _abort.Dispose(), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    private async Task<string> RunAsync(CancellationToken ct)
    {
        var key = _owner.Key ?? throw new TranscriptionException(TranscriptionErrorKind.NoKey, $"live {Name}: no key", service: Service);
        var socket = _owner.NewSocket();
        await using (socket.ConfigureAwait(false))
        {
            try
            {
                using var step = CancellationTokenSource.CreateLinkedTokenSource(ct);
                step.CancelAfter(_owner.StepTimeout);
                await socket.ConnectAsync(_owner.Protocol.Uri(_settings), ServiceClient.AuthHeaders(Service, key), step.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is WebSocketException or HttpRequestException or OperationCanceledException && !ct.IsCancellationRequested)
            {
                Log.Warn($"live {Name}: connect failed: {ex.GetType().Name} {Redactor.Clean(ex.Message, 160)}");
                throw new TranscriptionException(TranscriptionErrorKind.Network, $"live {Name}: connect failed", inner: ex, service: Service);
            }

            return await StreamAsync(socket, ct).ConfigureAwait(false);
        }
    }

    private async Task<string> StreamAsync(ILiveSocket socket, CancellationToken ct)
    {
        var protocol = _owner.Protocol;
        var pieces = new List<string>();
        var progress = new StreamProgress();
        var endSent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ending = 0; // set before the end is sent, so a quick reply to it is read as one
        var messages = 0;
        Task<string?>? pending = null;

        // Cancelling a pending WebSocket receive aborts the socket, so the receive is never cancelled to wait for
        // something else: it stays pending while the loop waits for the end of audio or for quiet.
        var receive = Task.Run(async () =>
        {
            while (true)
            {
                pending ??= socket.ReceiveAsync(ct);
                if (endSent.Task.IsCompleted)
                {
                    var wait = progress.Owed switch
                    {
                        null => _owner.QuietAfterEnd,
                        0 => _owner.SettleAfterEnd,
                        _ => _owner.OwedAfterEnd,
                    };
                    if (await Task.WhenAny(pending, Task.Delay(wait, _owner.Clock, ct)).ConfigureAwait(false) != pending)
                    {
                        ct.ThrowIfCancellationRequested();
                        return; // finished: nothing more came after the end of audio
                    }
                }
                else if (await Task.WhenAny(pending, endSent.Task).ConfigureAwait(false) != pending)
                {
                    continue; // the end of audio was sent: from now on, wait only until the service goes quiet
                }

                var message = await pending.ConfigureAwait(false);
                pending = null;
                if (message is null)
                {
                    var reason = socket.CloseReason;
                    if (pieces.Count == 0 && !string.IsNullOrEmpty(reason) && !reason.StartsWith("1000", StringComparison.Ordinal))
                    {
                        throw new TranscriptionException(StreamErrors.KindFor(reason), $"live {Name}: closed: {Redactor.Clean(reason, 200)}", service: Service);
                    }

                    return;
                }

                messages++;
                if (protocol.Apply(message, pieces, Volatile.Read(ref ending) == 1 || endSent.Task.IsCompleted, progress))
                {
                    return;
                }
            }
        }, ct);

        var buffer = new List<short>(ChunkSamples * 2);
        try
        {
            await foreach (var samples in _audio.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                buffer.AddRange(samples);
                if (buffer.Count >= ChunkSamples)
                {
                    await protocol.SendAudioAsync(socket, Drain(buffer), ct).ConfigureAwait(false);
                }

                if (receive.IsCompleted)
                {
                    break; // the service closed early; the error (if any) surfaces below
                }
            }

            if (buffer.Count > 0 && !receive.IsCompleted)
            {
                await protocol.SendAudioAsync(socket, Drain(buffer), ct).ConfigureAwait(false);
            }

            Volatile.Write(ref ending, 1);
            if (!receive.IsCompleted)
            {
                await socket.SendAsync(protocol.EndMessage, ct).ConfigureAwait(false);
            }

            endSent.TrySetResult();
            await receive.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is WebSocketException or IOException)
        {
            throw new TranscriptionException(TranscriptionErrorKind.Network, $"live {Name}: send failed", inner: ex, service: Service);
        }
        finally
        {
            endSent.TrySetResult();
            if (pending is { } p)
            {
                _ = p.ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            }
        }

        if (protocol.CloseMessage is { } close)
        {
            try
            {
                await socket.SendAsync(close, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is WebSocketException or IOException or InvalidOperationException)
            {
                // Best effort: the socket is closed next anyway.
            }
        }

        var text = EngineSupport.JoinParts(pieces);
        Log.Info($"live {Name}: finished, {text.Length} chars in {pieces.Count} parts ({messages} messages)");
        return text;
    }

    private static byte[] Drain(List<short> buffer)
    {
        var bytes = new byte[buffer.Count * 2];
        for (var i = 0; i < buffer.Count; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2), buffer[i]);
        }

        buffer.Clear();
        return bytes;
    }
}

internal static class StreamErrors
{
    public static TranscriptionErrorKind KindFor(string reason)
    {
        var r = reason.ToLowerInvariant();
        bool Has(string s) => r.Contains(s, StringComparison.Ordinal);
        if (Has("auth") || Has("key") || Has("401") || Has("403") || Has("credential"))
        {
            return TranscriptionErrorKind.InvalidKey;
        }

        if (Has("credit") || Has("balance") || Has("quota") || Has("402"))
        {
            return TranscriptionErrorKind.NoCredits;
        }

        return Has("rate") || Has("429") ? TranscriptionErrorKind.RateLimited : TranscriptionErrorKind.UnexpectedResponse;
    }

    public static Uri Wss(string baseUrl, string fallback, string path, IEnumerable<KeyValuePair<string, string>> query)
    {
        var b = new Uri(PlainspokenSettings.NormalizeBaseUrl(baseUrl, fallback));
        var scheme = b.Scheme == System.Uri.UriSchemeHttp ? "ws" : "wss";
        var q = string.Join("&", query.Select(p => System.Uri.EscapeDataString(p.Key) + "=" + System.Uri.EscapeDataString(p.Value)));
        return new Uri($"{scheme}://{b.Authority}{b.AbsolutePath.TrimEnd('/')}/{path}?{q}");
    }

    public static JsonElement? ParseObject(string message)
    {
        try
        {
            using var doc = JsonDocument.Parse(message);
            return doc.RootElement.ValueKind == JsonValueKind.Object ? doc.RootElement.Clone() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// Sarvam streaming (wss://api.sarvam.ai/speech-to-text/ws): raw 16 kHz PCM sent base64 in JSON audio messages,
/// {"type":"flush"} at the end; replies {"type":"data","data":{"transcript":…}} per stretch of speech, and
/// {"type":"events","data":{"signal_type":"START_SPEECH"|"END_SPEECH"}} when each starts and ends (vad_signals), which
/// is how the end is recognised: Sarvam doesn't confirm the flush.
/// </summary>
public sealed class SarvamStreaming : IStreamProtocol
{
    public static SarvamStreaming Instance { get; } = new();

    public SpeechService Service => SpeechService.Sarvam;

    public string EndMessage => """{"type":"flush"}""";

    public string? CloseMessage => null;

    public Uri Uri(PlainspokenSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var config = settings.Transcription.Services.Sarvam;
        var request = TranscriptionRequest.From([], 0, settings);
        var q = new List<KeyValuePair<string, string>>
        {
            new("language-code", SarvamEngine.Language(request.Languages)),
            new("model", config.Model),
            new("mode", SarvamEngine.Mode(request, config)),
            new("sample_rate", "16000"),
            new("input_audio_codec", "pcm_s16le"),
            new("flush_signal", "true"),
            new("vad_signals", "true"),
        };
        if (config.VocabularyHints && config.Model.Contains("v4", StringComparison.Ordinal))
        {
            var terms = EngineSupport.HintTerms(request.Vocabulary, max: 50, maxLength: 64);
            if (terms.Count > 0)
            {
                q.Add(new("keyterms", JsonSerializer.Serialize(terms)));
            }
        }

        return StreamErrors.Wss(config.ApiBaseUrl, Service.Info().DefaultBaseUrl, "speech-to-text/ws", q);
    }

    public Task SendAudioAsync(ILiveSocket socket, byte[] pcm, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(socket);
        var message = new JsonObject
        {
            ["audio"] = new JsonObject { ["data"] = Convert.ToBase64String(pcm), ["sample_rate"] = 16000, ["encoding"] = "audio/wav" },
        };
        return socket.SendAsync(message.ToJsonString(), ct);
    }

    public bool Apply(string message, List<string> pieces, bool endSent, StreamProgress progress)
    {
        ArgumentNullException.ThrowIfNull(pieces);
        ArgumentNullException.ThrowIfNull(progress);
        if (StreamErrors.ParseObject(message) is not { } root)
        {
            return false;
        }

        var data = root.TryGetProperty("data", out var d) ? d : default;
        switch (EngineSupport.Str(root, "type"))
        {
            case "data":
                if (EngineSupport.Str(data, "transcript") is { } text && !string.IsNullOrWhiteSpace(text))
                {
                    pieces.Add(text);
                }

                progress.Transcribed(endSent);
                break;
            case "events":
                switch (EngineSupport.Str(data, "signal_type"))
                {
                    case "START_SPEECH":
                        progress.SpeechStarted();
                        break;
                    case "END_SPEECH":
                        progress.SpeechEnded();
                        break;
                }

                break;
            case "error":
                var error = $"{EngineSupport.Str(data, "code")} {EngineSupport.Str(data, "error")}";
                throw new TranscriptionException(StreamErrors.KindFor(error), "live sarvam: " + Redactor.Clean(error, 200), service: Service);
        }

        return false; // Sarvam doesn't confirm the flush; the session stops once no transcript is owed
    }
}

/// <summary>
/// Deepgram streaming (wss://api.deepgram.com/v1/listen): binary linear16 frames, {"type":"Finalize"} at the end;
/// final Results carry the text, and the one answering Finalize has from_finalize = true.
/// </summary>
public sealed class DeepgramStreaming : IStreamProtocol
{
    public static DeepgramStreaming Instance { get; } = new();

    public SpeechService Service => SpeechService.Deepgram;

    public string EndMessage => """{"type":"Finalize"}""";

    public string? CloseMessage => """{"type":"CloseStream"}""";

    public Uri Uri(PlainspokenSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var config = settings.Transcription.Services.Deepgram;
        var q = DeepgramEngine.Query(TranscriptionRequest.From([], 0, settings), config);
        q.Add(new("encoding", "linear16"));
        q.Add(new("sample_rate", "16000"));
        q.Add(new("channels", "1"));
        q.Add(new("interim_results", "false"));
        return StreamErrors.Wss(config.ApiBaseUrl, Service.Info().DefaultBaseUrl, "v1/listen", q);
    }

    public Task SendAudioAsync(ILiveSocket socket, byte[] pcm, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(socket);
        return socket.SendBinaryAsync(pcm, ct);
    }

    public bool Apply(string message, List<string> pieces, bool endSent, StreamProgress progress)
    {
        ArgumentNullException.ThrowIfNull(pieces);
        if (StreamErrors.ParseObject(message) is not { } root)
        {
            return false;
        }

        switch (EngineSupport.Str(root, "type"))
        {
            case "Results":
                var isFinal = root.TryGetProperty("is_final", out var f) && f.ValueKind == JsonValueKind.True;
                string? text = null;
                if (root.TryGetProperty("channel", out var channel) && channel.ValueKind == JsonValueKind.Object &&
                    channel.TryGetProperty("alternatives", out var alternatives) && alternatives.ValueKind == JsonValueKind.Array &&
                    alternatives.GetArrayLength() > 0)
                {
                    text = EngineSupport.Str(alternatives[0], "transcript");
                }

                if (isFinal && !string.IsNullOrWhiteSpace(text))
                {
                    pieces.Add(text);
                }

                return endSent && root.TryGetProperty("from_finalize", out var ff) && ff.ValueKind == JsonValueKind.True;
            case "Error":
                var error = $"{EngineSupport.Str(root, "err_code") ?? EngineSupport.Str(root, "variant")} " +
                            $"{EngineSupport.Str(root, "err_msg") ?? EngineSupport.Str(root, "description")}";
                throw new TranscriptionException(StreamErrors.KindFor(error), "live deepgram: " + Redactor.Clean(error, 200), service: Service);
        }

        return false;
    }
}

/// <summary>
/// Picks the streaming service for a dictation: the first usable service, if it can stream and streaming is on for it.
/// Its raw text then goes through the same clean-up as everything else.
/// </summary>
public sealed class LiveRouter(IReadOnlyDictionary<SpeechService, ILiveTranscriber> transcribers, Func<PlainspokenSettings, SpeechService?> firstService)
    : ILiveTranscriber
{
    public bool CanStart(PlainspokenSettings settings) => Pick(settings)?.CanStart(settings) == true;

    public ILiveSession Start(PlainspokenSettings settings) =>
        (Pick(settings) ?? throw new InvalidOperationException("No streaming service.")).Start(settings);

    private ILiveTranscriber? Pick(PlainspokenSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return firstService(settings) is { } service && StreamingOn(settings, service) && transcribers.TryGetValue(service, out var t) ? t : null;
    }

    public static bool StreamingOn(PlainspokenSettings settings, SpeechService service)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return service switch
        {
            SpeechService.Gemini => settings.Transcription.LiveStreaming,
            SpeechService.Sarvam or SpeechService.Deepgram => settings.Transcription.Services[service].Streaming,
            _ => false,
        };
    }
}
