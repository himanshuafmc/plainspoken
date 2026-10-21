using System.Net.WebSockets;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Plainspoken.Core.Logging;
using Plainspoken.Core.Settings;
using Plainspoken.Core.Transcription;

namespace Plainspoken.Core.Tests;

/// <summary>A scripted streaming server: <c>onText</c> answers each text frame; binary frames are counted.</summary>
internal sealed class FakeStreamSocket(Func<string, IEnumerable<string>>? onText = null, bool failConnect = false, string? closeWith = null) : ILiveSocket
{
    private readonly Channel<string?> _incoming = Channel.CreateUnbounded<string?>();

    public List<string> SentText { get; } = [];

    public int BinaryFrames { get; private set; }

    public Uri? Uri { get; private set; }

    public IReadOnlyDictionary<string, string> Headers { get; private set; } = new Dictionary<string, string>();

    public string? CloseReason { get; private set; }

    public Task ConnectAsync(Uri uri, IReadOnlyDictionary<string, string> headers, CancellationToken ct)
    {
        Uri = uri;
        Headers = headers;
        if (failConnect)
        {
            return Task.FromException(new WebSocketException("refused"));
        }

        if (closeWith is not null)
        {
            CloseReason = closeWith;
            _incoming.Writer.TryWrite(null);
        }

        return Task.CompletedTask;
    }

    public Task SendAsync(string json, CancellationToken ct)
    {
        lock (SentText)
        {
            SentText.Add(json);
        }

        foreach (var reply in onText?.Invoke(json) ?? [])
        {
            _incoming.Writer.TryWrite(reply);
        }

        return Task.CompletedTask;
    }

    public Task SendBinaryAsync(byte[] bytes, CancellationToken ct)
    {
        BinaryFrames++;
        return Task.CompletedTask;
    }

    public async Task<string?> ReceiveAsync(CancellationToken ct) => await _incoming.Reader.ReadAsync(ct);

    /// <summary>A message the server sends on its own, not in answer to a frame.</summary>
    public void ServerSends(string message) => _incoming.Writer.TryWrite(message);

    public ValueTask DisposeAsync()
    {
        _incoming.Writer.TryWrite(null);
        return ValueTask.CompletedTask;
    }
}

public class StreamingTests
{
    private const string SpeechStarted = """{"type":"events","data":{"signal_type":"START_SPEECH","occured_at":0.4}}""";
    private const string SpeechEnded = """{"type":"events","data":{"signal_type":"END_SPEECH","occured_at":2.1}}""";

    private static string Transcript(string text) => new JsonObject { ["type"] = "data", ["data"] = new JsonObject { ["transcript"] = text } }.ToJsonString();

    private static PlainspokenSettings Settings() =>
        new PlainspokenSettings { Transcription = { ServiceOrder = ["sarvam", "deepgram"], CustomVocabulary = ["WhatsApp"] } }.Normalize();

    private static StreamingTranscriber Transcriber(IStreamProtocol protocol, FakeStreamSocket socket, TimeProvider? clock = null) =>
        new(protocol, () => socket, _ => "test-key", new ListLog())
        {
            StepTimeout = TimeSpan.FromSeconds(5),
            QuietAfterEnd = TimeSpan.FromMilliseconds(300),
            Clock = clock ?? TimeProvider.System,
        };

    private static void Push(ILiveSession session, double seconds)
    {
        for (var i = 0; i < (int)(seconds * 10); i++)
        {
            session.Push(Enumerable.Repeat((short)1000, 1600).ToArray());
        }
    }

    [Fact]
    public async Task Sarvam_streams_base64_audio_flushes_and_joins_the_parts()
    {
        var socket = new FakeStreamSocket(onText: msg => msg.Contains("\"flush\"", StringComparison.Ordinal)
            ? ["""{"type":"data","data":{"request_id":"a","transcript":"kal meeting hai","metrics":{}}}"""]
            : []);
        using var session = Transcriber(SarvamStreaming.Instance, socket).Start(Settings());
        Push(session, 1.0);
        Assert.Equal("kal meeting hai", await session.FinishAsync(default));
        Assert.Equal(SpeechService.Sarvam, session.Service);
        var uri = socket.Uri!.AbsoluteUri;
        Assert.StartsWith("wss://api.sarvam.ai/speech-to-text/ws?language-code=unknown&model=saaras%3Av3&mode=translit", uri, StringComparison.Ordinal);
        Assert.Contains("input_audio_codec=pcm_s16le", uri, StringComparison.Ordinal);
        Assert.Contains("flush_signal=true", uri, StringComparison.Ordinal);
        Assert.Equal("test-key", socket.Headers["api-subscription-key"]);
        Assert.StartsWith("""{"audio":{"data":""", socket.SentText[0], StringComparison.Ordinal);
        Assert.Equal("""{"type":"flush"}""", socket.SentText[^1]);
    }

    [Fact]
    public async Task Sarvam_waits_for_a_transcript_it_still_owes_after_the_flush()
    {
        // The transcript comes after QuietAfterEnd (300 ms here); the speech signals say it is still owed.
        FakeStreamSocket? socket = null;
        var started = false;
        socket = new FakeStreamSocket(onText: msg =>
        {
            if (msg.Contains("\"flush\"", StringComparison.Ordinal))
            {
                _ = Task.Run(async () =>
                {
                    await Task.Delay(700);
                    socket!.ServerSends(Transcript("kal meeting hai"));
                });
                return [];
            }

            if (started)
            {
                return [];
            }

            started = true;
            return [SpeechStarted];
        });
        using var session = Transcriber(SarvamStreaming.Instance, socket).Start(Settings());
        Push(session, 1.0);
        Assert.Equal("kal meeting hai", await session.FinishAsync(default));
        Assert.Contains("vad_signals=true", socket.Uri!.AbsoluteUri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sarvam_stops_as_soon_as_no_transcript_is_owed()
    {
        var socket = new FakeStreamSocket(onText: msg => msg.Contains("\"flush\"", StringComparison.Ordinal)
            ? [SpeechStarted, SpeechEnded, Transcript("theek hai")]
            : []);
        var t = new StreamingTranscriber(SarvamStreaming.Instance, () => socket, _ => "test-key", new ListLog())
        {
            QuietAfterEnd = TimeSpan.FromSeconds(3),
            SettleAfterEnd = TimeSpan.FromMilliseconds(100),
        };
        using var session = t.Start(Settings());
        Push(session, 0.5);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Assert.Equal("theek hai", await session.FinishAsync(default));
        Assert.True(watch.ElapsedMilliseconds < 1500, $"waited {watch.ElapsedMilliseconds} ms for quiet instead of stopping");
    }

    [Fact]
    public void Sarvams_speech_signals_say_how_many_transcripts_are_still_to_come()
    {
        var p = new StreamProgress();
        var pieces = new List<string>();
        var sarvam = SarvamStreaming.Instance;
        Assert.Null(p.Owed);
        sarvam.Apply(SpeechStarted, pieces, endSent: false, p);
        Assert.Equal(1, p.Owed);
        sarvam.Apply(Transcript("part"), pieces, endSent: false, p); // a long stretch may come in parts
        Assert.Equal(1, p.Owed);
        sarvam.Apply(SpeechEnded, pieces, endSent: false, p);
        sarvam.Apply(SpeechStarted, pieces, endSent: false, p);
        Assert.Equal(2, p.Owed);
        sarvam.Apply(Transcript("one"), pieces, endSent: false, p);
        Assert.Equal(1, p.Owed);
        sarvam.Apply(Transcript("two"), pieces, endSent: true, p); // the flush ends the stretch still going on
        Assert.Equal(0, p.Owed);
        Assert.Equal(["part", "one", "two"], pieces);
    }

    [Fact]
    public async Task Deepgram_streams_binary_audio_and_stops_when_Finalize_is_answered()
    {
        var socket = new FakeStreamSocket(onText: msg => msg.Contains("Finalize", StringComparison.Ordinal)
            ?
            [
                """{"type":"Results","is_final":true,"channel":{"alternatives":[{"transcript":"Hello there.","confidence":0.9}]}}""",
                """{"type":"Results","is_final":true,"from_finalize":true,"channel":{"alternatives":[{"transcript":"How are you?","confidence":0.9}]}}""",
            ]
            : []);
        using var session = Transcriber(DeepgramStreaming.Instance, socket).Start(Settings());
        Push(session, 0.5);
        Assert.Equal("Hello there. How are you?", await session.FinishAsync(default));
        Assert.True(socket.BinaryFrames >= 4);
        Assert.Equal("Token test-key", socket.Headers["Authorization"]);
        var uri = socket.Uri!.AbsoluteUri;
        Assert.StartsWith("wss://api.deepgram.com/v1/listen?model=nova-3", uri, StringComparison.Ordinal);
        Assert.Contains("encoding=linear16", uri, StringComparison.Ordinal);
        Assert.Contains("keyterm=WhatsApp", uri, StringComparison.Ordinal);
        Assert.Equal("""{"type":"CloseStream"}""", socket.SentText[^1]);
    }

    [Fact]
    public async Task An_error_message_or_a_refused_connection_fails_the_stream()
    {
        var bad = new FakeStreamSocket(onText: msg => msg.Contains("flush", StringComparison.Ordinal)
            ? ["""{"type":"error","data":{"error":"Invalid API key","code":"auth"}}"""]
            : []);
        using var s1 = Transcriber(SarvamStreaming.Instance, bad).Start(Settings());
        Push(s1, 0.3);
        Assert.Equal(TranscriptionErrorKind.InvalidKey, (await Assert.ThrowsAsync<TranscriptionException>(() => s1.FinishAsync(default))).Kind);

        using var s2 = Transcriber(DeepgramStreaming.Instance, new FakeStreamSocket(failConnect: true)).Start(Settings());
        Push(s2, 0.3);
        Assert.Equal(TranscriptionErrorKind.Network, (await Assert.ThrowsAsync<TranscriptionException>(() => s2.FinishAsync(default))).Kind);

        using var s3 = Transcriber(DeepgramStreaming.Instance, new FakeStreamSocket(closeWith: "1008 Invalid credentials")).Start(Settings());
        Push(s3, 0.3);
        Assert.Equal(TranscriptionErrorKind.InvalidKey, (await Assert.ThrowsAsync<TranscriptionException>(() => s3.FinishAsync(default))).Kind);
    }

    [Fact]
    public async Task Repeated_failures_pause_streaming_for_a_while()
    {
        var clock = new FakeTime(new DateTimeOffset(2026, 10, 18, 10, 0, 0, TimeSpan.Zero));
        var t = Transcriber(DeepgramStreaming.Instance, new FakeStreamSocket(failConnect: true), clock);
        for (var i = 0; i < 2; i++)
        {
            using var s = t.Start(Settings());
            await Assert.ThrowsAsync<TranscriptionException>(() => s.FinishAsync(default));
        }

        Assert.False(t.CanStart(Settings()));
        clock.Now += TimeSpan.FromMinutes(16);
        Assert.True(t.CanStart(Settings()));
    }

    [Fact]
    public void Streaming_needs_a_key()
    {
        var t = new StreamingTranscriber(SarvamStreaming.Instance, () => new FakeStreamSocket(), _ => " ", NullLog.Instance);
        Assert.False(t.CanStart(Settings()));
    }

    private sealed class NeverStarts : ILiveTranscriber
    {
        public ILiveSession Start(PlainspokenSettings settings) => throw new NotSupportedException();
    }

    [Fact]
    public void The_live_router_streams_only_with_the_main_service_when_it_can()
    {
        var fake = new NeverStarts();
        SpeechService? first = SpeechService.Sarvam;
        var router = new LiveRouter(new Dictionary<SpeechService, ILiveTranscriber> { [SpeechService.Sarvam] = fake, [SpeechService.Gemini] = fake }, _ => first);
        var s = Settings();
        Assert.True(router.CanStart(s));
        s.Transcription.Services.Sarvam.Streaming = false;
        Assert.False(router.CanStart(s));
        first = SpeechService.ElevenLabs;
        Assert.False(router.CanStart(Settings()));
        first = SpeechService.Gemini;
        Assert.True(router.CanStart(Settings()));
        var off = Settings();
        off.Transcription.LiveStreaming = false;
        Assert.False(router.CanStart(off));
        first = null;
        Assert.False(router.CanStart(Settings()));
    }
}
