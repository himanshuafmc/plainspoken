using System.Buffers.Binary;
using System.Net.WebSockets;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Plainspoken.Core.Logging;
using Plainspoken.Core.Settings;
using Plainspoken.Core.Transcription;

namespace Plainspoken.Core.Tests;

public class LiveTranscriptionTests
{
    private static PlainspokenSettings Settings() => new PlainspokenSettings
    {
        Transcription = { LiveStreaming = true, CustomVocabulary = ["WhatsApp"] },
    }.Normalize();

    private static GeminiLiveTranscriber Transcriber(FakeLiveServer server, ILog? log = null) =>
        new(server.Create, () => "test-key", log ?? NullLog.Instance)
        {
            StepTimeout = TimeSpan.FromSeconds(5),
            QuietAfterEnd = TimeSpan.FromMilliseconds(200),
        };

    private static short[] Samples(int n, short value = 1000) => Enumerable.Repeat(value, n).ToArray();

    [Fact]
    public async Task Streams_audio_and_returns_the_model_text()
    {
        var server = new FakeLiveServer
        {
            OnAudioEnd = () =>
            [
                """{"serverContent":{"inputTranscription":{"text":"hello world"}}}""",
                """{"serverContent":{"modelTurn":{"parts":[{"text":"Hello, "},{"text":"thinking","thought":true},{"text":"world."}]}}}""",
                """{"serverContent":{"turnComplete":true}}""",
            ],
        };
        using var session = Transcriber(server).Start(Settings());
        session.Push(Samples(1000));
        session.Push(Samples(1000));
        session.Push(Samples(500));

        var text = await session.FinishAsync(CancellationToken.None);

        Assert.Equal("Hello, world.", text);
        var socket = Assert.Single(server.Sockets);
        Assert.Equal("wss://generativelanguage.googleapis.com/ws/google.ai.generativelanguage.v1beta.GenerativeService.BidiGenerateContent",
            socket.Uri!.AbsoluteUri);
        Assert.Equal("test-key", socket.Key);

        var setup = JsonNode.Parse(socket.Sent[0])!["setup"]!;
        Assert.Equal("models/gemini-3.5-transcribe-live", setup["model"]!.GetValue<string>());
        var config = setup["generationConfig"]!["transcriptionConfig"]!;
        Assert.Equal("smart", config["mode"]!.GetValue<string>());
        Assert.Equal("WhatsApp", config["customVocabulary"]![0]!.GetValue<string>());
        Assert.Equal("en-IN", config["languageCodes"]![0]!.GetValue<string>());

        var audio = socket.Sent.Skip(1).Where(m => m.Contains("\"audio\"", StringComparison.Ordinal)).ToList();
        var totalSamples = 0;
        foreach (var m in audio)
        {
            var a = JsonNode.Parse(m)!["realtimeInput"]!["audio"]!;
            Assert.Equal("audio/pcm;rate=16000", a["mimeType"]!.GetValue<string>());
            var bytes = Convert.FromBase64String(a["data"]!.GetValue<string>());
            Assert.Equal(1000, BinaryPrimitives.ReadInt16LittleEndian(bytes));
            totalSamples += bytes.Length / 2;
        }

        Assert.Equal(2500, totalSamples);
        Assert.Contains("audioStreamEnd", socket.Sent[^1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rejected_setup_shape_falls_back_to_the_next_and_is_remembered()
    {
        var server = new FakeLiveServer
        {
            OnSetup = setup => setup["generationConfig"]?["transcriptionConfig"] is null
                ? (true, null)
                : (false, "1007 Invalid JSON payload received. Unknown name \"transcriptionConfig\""),
            OnAudioEnd = () => ["""{"serverContent":{"inputTranscription":{"text":" hi"}}}""", """{"serverContent":{"turnComplete":true}}"""],
        };
        var log = new ListLog();
        var transcriber = Transcriber(server, log);

        using (var first = transcriber.Start(Settings()))
        {
            first.Push(Samples(1600));
            Assert.Equal("hi", await first.FinishAsync(CancellationToken.None));
        }

        Assert.Equal(2, server.Sockets.Count);
        Assert.Contains(log.Lines, l => l.Contains("rejected", StringComparison.Ordinal) && l.Contains("Unknown name", StringComparison.Ordinal));

        using (var second = transcriber.Start(Settings()))
        {
            second.Push(Samples(1600));
            Assert.Equal("hi", await second.FinishAsync(CancellationToken.None));
        }

        Assert.Equal(3, server.Sockets.Count); // the accepted shape was tried first
    }

    [Fact]
    public async Task Input_transcription_chunks_are_joined_when_there_is_no_model_text()
    {
        var server = new FakeLiveServer
        {
            OnAudioEnd = () =>
            [
                """{"serverContent":{"inputTranscription":{"text":"Mujhe kal"}}}""",
                """{"serverContent":{"inputTranscription":{"text":" office jana hai."}}}""",
                """{"serverContent":{"generationComplete":true}}""",
            ],
        };
        using var session = Transcriber(server).Start(Settings());
        session.Push(Samples(3200));
        Assert.Equal("Mujhe kal office jana hai.", await session.FinishAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Server_that_goes_quiet_after_text_still_finishes()
    {
        var server = new FakeLiveServer { OnAudioEnd = () => ["""{"serverContent":{"inputTranscription":{"text":"Quiet server."}}}"""] };
        using var session = Transcriber(server).Start(Settings());
        session.Push(Samples(1600));
        Assert.Equal("Quiet server.", await session.FinishAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Invalid_key_stops_immediately_with_invalid_key()
    {
        var server = new FakeLiveServer { OnSetup = _ => (false, "1008 API key not valid. Please pass a valid API key.") };
        using var session = Transcriber(server).Start(Settings());
        session.Push(Samples(1600));

        var ex = await Assert.ThrowsAsync<TranscriptionException>(() => session.FinishAsync(CancellationToken.None));
        Assert.Equal(TranscriptionErrorKind.InvalidKey, ex.Kind);
        Assert.Single(server.Sockets);
    }

    [Fact]
    public async Task Unreachable_endpoint_fails_so_the_caller_can_fall_back()
    {
        var server = new FakeLiveServer { FailConnect = true };
        using var session = Transcriber(server).Start(Settings());
        session.Push(Samples(1600));

        var ex = await Assert.ThrowsAsync<TranscriptionException>(() => session.FinishAsync(CancellationToken.None));
        Assert.Equal(TranscriptionErrorKind.Network, ex.Kind);
        Assert.Equal(2, server.Sockets.Count); // one attempt per API version, not per setup shape
    }

    [Fact]
    public async Task Repeated_failures_pause_live_streaming_for_a_while()
    {
        var clock = new ManualClock();
        var server = new FakeLiveServer { FailConnect = true };
        var transcriber = new GeminiLiveTranscriber(server.Create, () => "test-key", NullLog.Instance)
        {
            StepTimeout = TimeSpan.FromSeconds(5),
            QuietAfterEnd = TimeSpan.FromMilliseconds(200),
            Clock = clock,
        };
        var settings = Settings();

        async Task Dictate()
        {
            using var session = transcriber.Start(settings);
            session.Push(Samples(1600));
            try
            {
                await session.FinishAsync(CancellationToken.None);
            }
            catch (TranscriptionException)
            {
            }
        }

        await Dictate();
        Assert.True(transcriber.CanStart(settings)); // one failure is not enough
        await Dictate();
        Assert.False(transcriber.CanStart(settings));

        var otherModel = Settings();
        otherModel.Transcription.LiveModel = "gemini-other-live";
        Assert.True(transcriber.CanStart(otherModel)); // changed settings end the pause

        await Dictate();
        await Dictate();
        Assert.False(transcriber.CanStart(settings));
        clock.Advance(TimeSpan.FromMinutes(16));
        Assert.True(transcriber.CanStart(settings)); // tried again after the pause
    }

    [Fact]
    public async Task A_success_resets_the_failure_count()
    {
        var server = new FakeLiveServer
        {
            FailConnect = true,
            OnAudioEnd = () => ["""{"serverContent":{"inputTranscription":{"text":"ok"}}}""", """{"serverContent":{"turnComplete":true}}"""],
        };
        var transcriber = Transcriber(server);
        var settings = Settings();

        async Task Dictate()
        {
            using var session = transcriber.Start(settings);
            session.Push(Samples(1600));
            try
            {
                await session.FinishAsync(CancellationToken.None);
            }
            catch (TranscriptionException)
            {
            }
        }

        await Dictate();
        server.FailConnect = false;
        await Dictate();
        server.FailConnect = true;
        await Dictate();
        Assert.True(transcriber.CanStart(settings));
    }

    [Fact]
    public async Task Log_never_contains_transcript_text()
    {
        var log = new ListLog();
        var server = new FakeLiveServer
        {
            OnAudioEnd = () => ["""{"serverContent":{"modelTurn":{"parts":[{"text":"secret words"}]},"turnComplete":true}}"""],
        };
        using var session = Transcriber(server, log).Start(Settings());
        session.Push(Samples(1600));
        Assert.Equal("secret words", await session.FinishAsync(CancellationToken.None));
        Assert.DoesNotContain(log.Lines, l => l.Contains("secret", StringComparison.Ordinal));
        Assert.Contains(log.Lines, l => l.Contains("message shape", StringComparison.Ordinal));
    }

    [Fact]
    public void Endpoint_follows_the_base_url()
    {
        Assert.Equal("wss://example.test/ws/google.ai.generativelanguage.v1alpha.GenerativeService.BidiGenerateContent",
            GeminiLiveTranscriber.Endpoint("https://example.test/", "v1alpha").AbsoluteUri);
    }
}

/// <summary>Scripted Live API server for tests.</summary>
internal sealed class FakeLiveServer
{
    public Func<JsonNode, (bool Accept, string? CloseReason)> OnSetup { get; init; } = _ => (true, null);

    public Func<IEnumerable<string>> OnAudioEnd { get; init; } = () => ["""{"serverContent":{"turnComplete":true}}"""];

    public bool FailConnect { get; set; }

    public List<FakeLiveSocket> Sockets { get; } = [];

    public ILiveSocket Create()
    {
        var socket = new FakeLiveSocket(this);
        lock (Sockets)
        {
            Sockets.Add(socket);
        }

        return socket;
    }
}

internal sealed class FakeLiveSocket(FakeLiveServer server) : ILiveSocket
{
    private readonly Channel<string?> _incoming = Channel.CreateUnbounded<string?>();
    private bool _closed;

    public List<string> Sent { get; } = [];

    public Uri? Uri { get; private set; }

    public string? Key { get; private set; }

    public string? CloseReason { get; private set; }

    public Task ConnectAsync(Uri uri, string? apiKey, CancellationToken ct)
    {
        Uri = uri;
        Key = apiKey;
        return server.FailConnect ? Task.FromException(new WebSocketException("404")) : Task.CompletedTask;
    }

    public Task SendAsync(string json, CancellationToken ct)
    {
        lock (Sent)
        {
            Sent.Add(json);
        }

        var node = JsonNode.Parse(json)!;
        if (node["setup"] is { } setup)
        {
            var (accept, reason) = server.OnSetup(setup);
            if (accept)
            {
                _incoming.Writer.TryWrite("""{"setupComplete":{}}""");
            }
            else
            {
                CloseReason = reason;
                _incoming.Writer.TryWrite(null);
            }
        }
        else if (node["realtimeInput"]?["audioStreamEnd"] is not null)
        {
            foreach (var m in server.OnAudioEnd())
            {
                _incoming.Writer.TryWrite(m);
            }
        }

        return Task.CompletedTask;
    }

    public async Task<string?> ReceiveAsync(CancellationToken ct)
    {
        if (_closed)
        {
            return null;
        }

        var message = await _incoming.Reader.ReadAsync(ct);
        _closed = message is null;
        return message;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class ManualClock : TimeProvider
{
    private DateTimeOffset _now = new(2026, 9, 30, 6, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}
