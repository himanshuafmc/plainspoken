using System.Buffers.Binary;
using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Plainspoken.Core.Logging;
using Plainspoken.Core.Settings;

namespace Plainspoken.Core.Transcription;

/// <summary>
/// A streaming transcription that receives audio while the user is still speaking,
/// so the text is (nearly) ready when they stop. Experimental: callers must fall back
/// to the normal engine if <see cref="FinishAsync"/> throws or returns nothing.
/// </summary>
public interface ILiveSession : IDisposable
{
    /// <summary>Queues 16 kHz mono PCM16 samples. Safe to call from any thread; never blocks or throws.</summary>
    void Push(short[] samples);

    /// <summary>Signals end of audio and waits for the final transcript.</summary>
    Task<string> FinishAsync(CancellationToken ct);

    /// <summary>Stops sending and closes the connection (cancel / clip too short).</summary>
    void Abort();
}

public interface ILiveTranscriber
{
    /// <summary>False while live streaming is paused after repeated failures; the normal engine is used then.</summary>
    bool CanStart(PlainspokenSettings settings) => true;

    /// <summary>Starts connecting immediately; audio pushed before the connection is ready is buffered.</summary>
    ILiveSession Start(PlainspokenSettings settings);
}

/// <summary>Minimal WebSocket abstraction so the protocol can be unit-tested without a network.</summary>
public interface ILiveSocket : IAsyncDisposable
{
    Task ConnectAsync(Uri uri, string? apiKey, CancellationToken ct);

    Task SendAsync(string json, CancellationToken ct);

    /// <summary>Next text/binary message decoded as UTF-8, or null once the server has closed the connection.</summary>
    Task<string?> ReceiveAsync(CancellationToken ct);

    /// <summary>Close status and reason sent by the server, if any.</summary>
    string? CloseReason { get; }
}

/// <summary>Real socket (System.Net.WebSockets; uses the system proxy like HttpClient).</summary>
public sealed class WebSocketLiveSocket : ILiveSocket
{
    private readonly ClientWebSocket _ws = new();

    public string? CloseReason { get; private set; }

    public async Task ConnectAsync(Uri uri, string? apiKey, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            _ws.Options.SetRequestHeader(GeminiClient.ApiKeyHeader, apiKey.Trim());
        }

        _ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
        await _ws.ConnectAsync(uri, ct).ConfigureAwait(false);
    }

    public Task SendAsync(string json, CancellationToken ct) =>
        _ws.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, ct);

    public async Task<string?> ReceiveAsync(CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();
        while (true)
        {
            if (_ws.State is not (WebSocketState.Open or WebSocketState.CloseSent))
            {
                return null;
            }

            var result = await _ws.ReceiveAsync(buffer, ct).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                CloseReason = $"{(int?)result.CloseStatus} {result.CloseStatusDescription}".Trim();
                return null;
            }

            message.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                // The Live API sends JSON in text or binary frames; both are UTF-8.
                return Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_ws.State == WebSocketState.Open)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", cts.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
            // Closing is best effort.
        }

        _ws.Dispose();
    }
}

/// <summary>
/// gemini-3.5-transcribe-live over the Live API (BidiGenerateContent WebSocket).
/// The exact setup message for this model is not verified yet (the cloud dev VM cannot open WebSockets),
/// so several known shapes are tried in order and the first one the server accepts is remembered.
/// Every server message shape is logged (without text) so problems can be fixed from the user's log.
/// </summary>
public sealed class GeminiLiveTranscriber : ILiveTranscriber
{
    private static readonly string[] ApiVersions = ["v1beta", "v1alpha"];

    private readonly Func<ILiveSocket> _socketFactory;
    private readonly Func<string?> _apiKey;
    private readonly ILog _log;
    private readonly Lock _gate = new();
    private int _preferred; // index into Candidates() of the last setup the server accepted
    private int _failures; // failed dictations in a row
    private DateTimeOffset _pausedUntil;
    private string? _pausedFor; // settings the pause applies to; changing them ends the pause

    public GeminiLiveTranscriber(Func<ILiveSocket> socketFactory, Func<string?> apiKey, ILog log)
    {
        _socketFactory = socketFactory ?? throw new ArgumentNullException(nameof(socketFactory));
        _apiKey = apiKey ?? throw new ArgumentNullException(nameof(apiKey));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <summary>Connection timeout, setup timeout and "quiet after end" window (shortened in tests).</summary>
    public TimeSpan StepTimeout { get; init; } = TimeSpan.FromSeconds(10);

    public TimeSpan QuietAfterEnd { get; init; } = TimeSpan.FromSeconds(1.5);

    /// <summary>
    /// After this many failed dictations in a row live streaming pauses for <see cref="PauseAfterFailures"/>,
    /// so a network that blocks WebSockets doesn't add the live timeout to every dictation.
    /// </summary>
    public int FailuresBeforePause { get; init; } = 2;

    public TimeSpan PauseAfterFailures { get; init; } = TimeSpan.FromMinutes(15);

    public TimeProvider Clock { get; init; } = TimeProvider.System;

    public bool CanStart(PlainspokenSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        lock (_gate)
        {
            if (_pausedFor is null)
            {
                return true;
            }

            if (_pausedFor != PauseKey(settings) || Clock.GetUtcNow() >= _pausedUntil)
            {
                _pausedFor = null; // pause over, or the key/model/address changed: try live again
                _failures = 0;
                return true;
            }

            return false;
        }
    }

    /// <summary>Records how a finished dictation went. Cancelled dictations are not reported.</summary>
    internal void Report(PlainspokenSettings settings, bool success)
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
            _pausedFor = PauseKey(settings);
            _pausedUntil = Clock.GetUtcNow() + PauseAfterFailures;
        }

        _log.Warn($"live: {FailuresBeforePause} failures in a row; paused for {PauseAfterFailures.TotalMinutes:0} min (using the normal engine)");
    }

    // The key is only hashed in memory, never stored or logged.
    private string PauseKey(PlainspokenSettings settings) => string.Create(CultureInfo.InvariantCulture,
        $"{settings.Transcription.LiveModel}|{settings.Transcription.ApiBaseUrl}|{(_apiKey() ?? string.Empty).GetHashCode(StringComparison.Ordinal)}");

    public ILiveSession Start(PlainspokenSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new GeminiLiveSession(this, settings);
    }

    internal ILog Log => _log;

    internal string? ApiKey => _apiKey();

    internal ILiveSocket NewSocket() => _socketFactory();

    internal int Preferred
    {
        get => Volatile.Read(ref _preferred);
        set => Volatile.Write(ref _preferred, value);
    }

    internal static Uri Endpoint(string apiBaseUrl, string version)
    {
        var baseUri = new Uri(PlainspokenSettings.NormalizeBaseUrl(apiBaseUrl));
        var scheme = baseUri.Scheme == Uri.UriSchemeHttp ? "ws" : "wss";
        return new Uri($"{scheme}://{baseUri.Authority}/ws/google.ai.generativelanguage.{version}.GenerativeService.BidiGenerateContent");
    }

    /// <summary>(API version, setup message) pairs to try, most likely first.</summary>
    internal static IReadOnlyList<(string Version, string Name, JsonObject Setup)> Candidates(PlainspokenSettings settings)
    {
        var t = settings.Transcription;
        var model = "models/" + ModelId.Normalize(t.LiveModel);
        var request = TranscriptionRequest.From([], 0, settings);

        JsonObject TranscriptionConfig()
        {
            var config = new JsonObject { ["mode"] = request.Mode == TranscriptionMode.Verbatim ? "verbatim" : "smart" };
            if (request.Vocabulary.Count > 0)
            {
                config["customVocabulary"] = new JsonArray(request.Vocabulary.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());
            }

            if (request.LanguageCodes.Count > 0)
            {
                config["languageCodes"] = new JsonArray(request.LanguageCodes.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());
            }

            return config;
        }

        var shapes = new (string Name, Func<JsonObject> Build)[]
        {
            ("transcriptionConfig", () => new JsonObject
            {
                ["model"] = model,
                ["generationConfig"] = new JsonObject { ["transcriptionConfig"] = TranscriptionConfig() },
            }),
            ("inputAudioTranscription+text", () => new JsonObject
            {
                ["model"] = model,
                ["generationConfig"] = new JsonObject { ["responseModalities"] = new JsonArray("TEXT") },
                ["inputAudioTranscription"] = new JsonObject(),
            }),
            ("inputAudioTranscription", () => new JsonObject
            {
                ["model"] = model,
                ["inputAudioTranscription"] = new JsonObject(),
            }),
            ("model-only", () => new JsonObject { ["model"] = model }),
        };

        var list = new List<(string, string, JsonObject)>();
        foreach (var version in ApiVersions)
        {
            foreach (var (name, build) in shapes)
            {
                list.Add((version, name, new JsonObject { ["setup"] = build() }));
            }
        }

        return list;
    }
}

internal sealed class GeminiLiveSession : ILiveSession
{
    private const int ChunkSamples = 1600; // 100 ms at 16 kHz

    private readonly GeminiLiveTranscriber _owner;
    private readonly PlainspokenSettings _settings;
    private readonly Channel<short[]> _audio = Channel.CreateUnbounded<short[]>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _abort = new();
    private readonly Task<string> _run;
    private double _pushedSeconds;

    public GeminiLiveSession(GeminiLiveTranscriber owner, PlainspokenSettings settings)
    {
        _owner = owner;
        _settings = settings;
        _run = Task.Run(() => RunAsync(_abort.Token));
        _ = _run.ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
    }

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
        // Streaming should finish quickly; allow a little extra for long dictations.
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Min(30, 8 + (_pushedSeconds / 10))));
        try
        {
            var text = await _run.WaitAsync(timeout.Token).ConfigureAwait(false);
            _owner.Report(_settings, success: !string.IsNullOrWhiteSpace(text));
            return text;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && !_abort.IsCancellationRequested)
        {
            Abort();
            _owner.Report(_settings, success: false);
            throw new TranscriptionException(TranscriptionErrorKind.Timeout, "live: no final transcript in time");
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            _owner.Report(_settings, success: false);
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
        var candidates = GeminiLiveTranscriber.Candidates(_settings);
        var order = Enumerable.Range(0, candidates.Count).OrderBy(i => i == _owner.Preferred ? 0 : 1).ThenBy(i => i).ToList();
        var skipVersions = new HashSet<string>(StringComparer.Ordinal);
        TranscriptionException? last = null;

        foreach (var index in order)
        {
            var (version, name, setup) = candidates[index];
            if (skipVersions.Contains(version))
            {
                continue;
            }

            ct.ThrowIfCancellationRequested();
            var socket = _owner.NewSocket();
            await using (socket.ConfigureAwait(false))
            {
                try
                {
                    using (var step = StepToken(ct))
                    {
                        await socket.ConnectAsync(GeminiLiveTranscriber.Endpoint(_settings.Transcription.ApiBaseUrl, version), _owner.ApiKey, step.Token)
                            .ConfigureAwait(false);
                    }
                }
                catch (Exception ex) when (ex is WebSocketException or HttpRequestException or OperationCanceledException && !ct.IsCancellationRequested)
                {
                    Log.Warn($"live: connect {version} failed: {ex.GetType().Name} {Redactor.Clean(ex.Message, 160)}");
                    last = new TranscriptionException(TranscriptionErrorKind.Network, "live: connect failed", inner: ex);
                    skipVersions.Add(version); // endpoint missing or unreachable: other shapes won't help
                    continue;
                }

                string? reply;
                using (var step = StepToken(ct))
                {
                    await socket.SendAsync(setup.ToJsonString(), step.Token).ConfigureAwait(false);
                    reply = await socket.ReceiveAsync(step.Token).ConfigureAwait(false);
                }

                if (reply is null || !LiveTranscript.IsSetupComplete(reply))
                {
                    var reason = Redactor.Clean(socket.CloseReason ?? (reply is null ? "closed" : JsonShape.Describe(reply)), 300);
                    Log.Warn($"live: setup '{name}' on {version} rejected: {reason}");
                    last = LiveTranscript.ErrorFor(reason);
                    if (last.Kind is TranscriptionErrorKind.InvalidKey or TranscriptionErrorKind.RateLimited or TranscriptionErrorKind.DailyQuota)
                    {
                        throw last; // no other shape will fix these
                    }

                    continue;
                }

                if (_owner.Preferred != index)
                {
                    _owner.Preferred = index;
                    Log.Info($"live: setup '{name}' on {version} accepted");
                }

                return await StreamAsync(socket, ct).ConfigureAwait(false);
            }
        }

        throw last ?? new TranscriptionException(TranscriptionErrorKind.UnexpectedResponse, "live: no setup accepted");
    }

    private async Task<string> StreamAsync(ILiveSocket socket, CancellationToken ct)
    {
        var transcript = new LiveTranscript(Log);
        var endSent = false;
        var receive = Task.Run(async () =>
        {
            while (true)
            {
                string? message;
                if (Volatile.Read(ref endSent))
                {
                    // After the end of audio, stop once the server finishes or goes quiet.
                    using var quiet = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    quiet.CancelAfter(_owner.QuietAfterEnd);
                    try
                    {
                        message = await socket.ReceiveAsync(quiet.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        if (transcript.HasText)
                        {
                            Log.Info("live: server went quiet after end of audio; using the text received");
                            return;
                        }

                        continue;
                    }
                }
                else
                {
                    message = await socket.ReceiveAsync(ct).ConfigureAwait(false);
                }

                if (message is null)
                {
                    if (!transcript.HasText && socket.CloseReason is { Length: > 0 } reason)
                    {
                        throw LiveTranscript.ErrorFor(Redactor.Clean(reason, 300));
                    }

                    return;
                }

                if (transcript.Apply(message) && Volatile.Read(ref endSent))
                {
                    return; // turn/generation complete after we sent the end of audio
                }
            }
        }, ct);

        var buffer = new List<short>(ChunkSamples * 2);
        await foreach (var samples in _audio.Reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            buffer.AddRange(samples);
            if (buffer.Count >= ChunkSamples)
            {
                await SendAudioAsync(socket, buffer, ct).ConfigureAwait(false);
            }

            if (receive.IsCompleted)
            {
                break; // server closed early; the error (if any) surfaces below
            }
        }

        if (buffer.Count > 0 && !receive.IsCompleted)
        {
            await SendAudioAsync(socket, buffer, ct).ConfigureAwait(false);
        }

        if (!receive.IsCompleted)
        {
            await socket.SendAsync("""{"realtimeInput":{"audioStreamEnd":true}}""", ct).ConfigureAwait(false);
        }

        Volatile.Write(ref endSent, true);
        await receive.ConfigureAwait(false);
        Log.Info($"live: finished, {transcript.Text.Length} chars ({transcript.Messages} messages)");
        return transcript.Text;
    }

    private static async Task SendAudioAsync(ILiveSocket socket, List<short> buffer, CancellationToken ct)
    {
        var bytes = new byte[buffer.Count * 2];
        for (var i = 0; i < buffer.Count; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2), buffer[i]);
        }

        buffer.Clear();
        var message = new JsonObject
        {
            ["realtimeInput"] = new JsonObject
            {
                ["audio"] = new JsonObject { ["data"] = Convert.ToBase64String(bytes), ["mimeType"] = "audio/pcm;rate=16000" },
            },
        };
        await socket.SendAsync(message.ToJsonString(), ct).ConfigureAwait(false);
    }

    private CancellationTokenSource StepToken(CancellationToken ct)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_owner.StepTimeout);
        return cts;
    }
}

/// <summary>
/// Collects text from Live API server messages, tolerating several shapes:
/// serverContent.modelTurn.parts[].text (preferred: the model's formatted output),
/// serverContent.inputTranscription.text / transcription.text (speech-to-text chunks, concatenated).
/// </summary>
internal sealed class LiveTranscript
{
    private readonly ILog _log;
    private readonly StringBuilder _model = new();
    private readonly StringBuilder _input = new();
    private readonly HashSet<string> _loggedShapes = new(StringComparer.Ordinal);

    public LiveTranscript(ILog log) => _log = log;

    public int Messages { get; private set; }

    public bool HasText => _model.Length > 0 || _input.Length > 0;

    public string Text => (_model.Length > 0 ? _model : _input).ToString().Trim();

    public static bool IsSetupComplete(string message)
    {
        try
        {
            using var doc = JsonDocument.Parse(message);
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("setupComplete", out _);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static TranscriptionException ErrorFor(string reason)
    {
        var r = reason.ToUpperInvariant();
        var kind = r.Contains("API KEY", StringComparison.Ordinal) || r.Contains("PERMISSION", StringComparison.Ordinal)
            ? TranscriptionErrorKind.InvalidKey
            : r.Contains("QUOTA", StringComparison.Ordinal) || r.Contains("RESOURCE_EXHAUSTED", StringComparison.Ordinal) || r.Contains("RATE LIMIT", StringComparison.Ordinal)
                ? GeminiErrors.Classify429(reason, [], GeminiErrors.ParseRetryFromMessage(reason))
                : r.Contains("NOT FOUND", StringComparison.Ordinal) || r.Contains("NOT SUPPORTED", StringComparison.Ordinal)
                    ? TranscriptionErrorKind.ModelNotFound
                    : TranscriptionErrorKind.UnexpectedResponse;
        return new TranscriptionException(kind, "live: " + reason);
    }

    /// <summary>Returns true when the message marks the turn/generation as complete.</summary>
    public bool Apply(string message)
    {
        Messages++;
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(message);
        }
        catch (JsonException)
        {
            _log.Warn($"live: non-JSON message ({message.Length} chars)");
            return false;
        }

        using (doc)
        {
            var root = doc.RootElement;
            var shape = JsonShape.Describe(root);
            if (_loggedShapes.Count < 12 && _loggedShapes.Add(shape))
            {
                _log.Info("live: message shape " + shape);
            }

            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (root.TryGetProperty("error", out _))
            {
                throw ErrorFor(JsonShape.Describe(root));
            }

            var complete = false;
            if (root.TryGetProperty("serverContent", out var content) && content.ValueKind == JsonValueKind.Object)
            {
                AppendText(content, "inputTranscription", _input);
                AppendText(content, "transcription", _input);
                if (content.TryGetProperty("modelTurn", out var turn) && turn.ValueKind == JsonValueKind.Object &&
                    turn.TryGetProperty("parts", out var parts) && parts.ValueKind == JsonValueKind.Array)
                {
                    foreach (var part in parts.EnumerateArray())
                    {
                        if (part.ValueKind == JsonValueKind.Object &&
                            !(part.TryGetProperty("thought", out var th) && th.ValueKind == JsonValueKind.True) &&
                            part.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                        {
                            _model.Append(text.GetString());
                        }
                    }
                }

                complete = IsTrue(content, "turnComplete") || IsTrue(content, "generationComplete");
            }

            // Tolerate a top-level transcription object too.
            AppendText(root, "inputTranscription", _input);
            AppendText(root, "transcription", _input);
            return complete;
        }
    }

    private static void AppendText(JsonElement parent, string name, StringBuilder target)
    {
        if (parent.TryGetProperty(name, out var obj) && obj.ValueKind == JsonValueKind.Object &&
            obj.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
        {
            target.Append(text.GetString());
        }
    }

    private static bool IsTrue(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}
