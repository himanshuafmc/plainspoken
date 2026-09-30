using Plainspoken.Core.Dictation;
using Plainspoken.Core.Logging;
using Plainspoken.Core.Settings;
using Plainspoken.Core.Storage;
using Plainspoken.Core.Transcription;

namespace Plainspoken.Core.Tests;

public sealed class DictationControllerTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly FakeRecorder _recorder = new();
    private readonly FakeTranscriber _transcriber = new();
    private readonly FakeInserter _inserter = new();
    private readonly FakeView _view = new();
    private readonly FakeSounds _sounds = new();
    private readonly PlainspokenSettings _settings = new PlainspokenSettings().Normalize();
    private readonly HistoryStore _history;
    private readonly PendingStore _pending;
    private readonly ListLog _log = new();
    private bool _hasKey = true;

    public DictationControllerTests()
    {
        _history = new HistoryStore(_dir.File("history.json"), _log);
        _pending = new PendingStore(_dir.File("pending"), _log);
        _transcriber.PendingCount = () => _pending.Count;
        _settings.Transcription.LiveStreaming = false; // most tests exercise the normal engine; live tests turn it on
    }

    public void Dispose() => _dir.Dispose();

    private readonly FakeLiveTranscriber _liveTranscriber = new();

    private DictationController Create() => new(() => _settings, () => _hasKey, _recorder, _transcriber, _inserter, _view, _sounds,
        _history, _pending, _log, new FakeTime(new DateTimeOffset(2026, 7, 15, 10, 0, 0, TimeSpan.Zero),
            Time.QuotaReset.FindZone("Asia/Kolkata", "India Standard Time")), _liveTranscriber);

    [Fact]
    public async Task Live_text_is_used_and_the_normal_engine_is_not_called()
    {
        _settings.Transcription.LiveStreaming = true;
        _liveTranscriber.Result = "From the live model.";
        var c = Create();
        _recorder.Audio = Speech(1);

        await c.ToggleAsync();
        await c.ToggleAsync();

        var session = Assert.Single(_liveTranscriber.Sessions);
        Assert.True(session.Pushed > 0);          // audio streamed while recording
        Assert.True(session.Disposed);
        Assert.Equal(0, _transcriber.Calls);
        Assert.Equal(["From the live model. "], _inserter.Inserted);
        Assert.Equal(0, _pending.Count);
        Assert.Null(_recorder.SampleSink);
    }

    [Fact]
    public async Task Live_failure_or_empty_text_falls_back_to_the_normal_engine()
    {
        _settings.Transcription.LiveStreaming = true;
        var c = Create();
        _recorder.Audio = Speech(1);
        _transcriber.Result = "Batch text.";

        _liveTranscriber.Error = new TranscriptionException(TranscriptionErrorKind.UnexpectedResponse, "live: bad setup");
        await c.ToggleAsync();
        await c.ToggleAsync();

        _liveTranscriber.Error = null;
        _liveTranscriber.Result = "   ";
        await c.ToggleAsync();
        await c.ToggleAsync();

        Assert.Equal(2, _transcriber.Calls);
        Assert.Equal(["Batch text. ", "Batch text. "], _inserter.Inserted);
        Assert.Contains(_log.Lines, l => l.Contains("using the normal engine", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Live_off_uses_only_the_normal_engine_and_warm_up_runs_on_start()
    {
        var c = Create();
        _recorder.Audio = Speech(1);
        await c.ToggleAsync();
        await c.ToggleAsync();

        Assert.Empty(_liveTranscriber.Sessions);
        Assert.Equal(1, _transcriber.WarmUps);
        Assert.Equal(1, _transcriber.Calls);
    }

    [Fact]
    public async Task Paused_live_streaming_goes_straight_to_the_normal_engine()
    {
        _settings.Transcription.LiveStreaming = true;
        _liveTranscriber.Available = false;
        var c = Create();
        _recorder.Audio = Speech(1);
        await c.ToggleAsync();
        await c.ToggleAsync();

        Assert.Empty(_liveTranscriber.Sessions);
        Assert.Null(_recorder.SampleSink);
        Assert.Equal(1, _transcriber.Calls);
    }

    [Fact]
    public async Task Cancel_and_short_clips_abort_the_live_session()
    {
        _settings.Transcription.LiveStreaming = true;
        var c = Create();
        await c.ToggleAsync();
        await c.CancelAsync();

        _recorder.Audio = Speech(0.3);
        await c.ToggleAsync();
        await c.ToggleAsync();

        Assert.Equal(2, _liveTranscriber.Sessions.Count);
        Assert.All(_liveTranscriber.Sessions, s => Assert.True(s.Disposed));
        Assert.All(_liveTranscriber.Sessions, s => Assert.False(s.Finished));
        Assert.Equal(0, _transcriber.Calls);
    }

    private static short[] Speech(double seconds)
    {
        var rnd = new Random(3);
        return Enumerable.Range(0, (int)(16000 * seconds)).Select(_ => (short)rnd.Next(-3000, 3000)).ToArray();
    }

    [Fact]
    public async Task Toggle_twice_records_transcribes_and_inserts()
    {
        var c = Create();
        _recorder.Audio = Speech(2);
        _transcriber.Result = "  Hello world.  ";

        await c.ToggleAsync();
        Assert.Equal(DictationState.Recording, c.State);
        Assert.True(_recorder.IsRecording);
        Assert.Equal(1, _sounds.Starts);
        Assert.Contains("recording", _view.Events);

        await c.ToggleAsync();

        Assert.Equal(DictationState.Idle, c.State);
        Assert.False(_recorder.IsRecording);
        Assert.Equal(1, _sounds.Stops);
        Assert.Equal(["Hello world. "], _inserter.Inserted);
        Assert.Equal("Hello world.", _history.Entries.Single().Text);
        Assert.Equal(0, _pending.Count);                  // audio deleted after success
        Assert.True(_transcriber.PendingExistedDuringCall); // but it was saved before the network call
        Assert.Equal([DictationState.Recording, DictationState.Transcribing, DictationState.Idle], _view.States);
        Assert.Equal(TranscriptionMode.Smart, _transcriber.LastRequest!.Mode);
        Assert.Equal(["en-IN", "hi-IN"], _transcriber.LastRequest.LanguageCodes);
    }

    [Fact]
    public async Task Trailing_space_and_sounds_follow_settings()
    {
        _settings.Insertion.TrailingSpace = false;
        _settings.Recording.Sounds = false;
        var c = Create();
        _recorder.Audio = Speech(1);
        _transcriber.Result = "Hi.";

        await c.ToggleAsync();
        await c.ToggleAsync();

        Assert.Equal(["Hi."], _inserter.Inserted);
        Assert.Equal(0, _sounds.Starts + _sounds.Stops);
    }

    [Fact]
    public async Task Short_or_silent_clip_makes_no_api_call()
    {
        var c = Create();
        _recorder.Audio = Speech(0.4);
        await c.ToggleAsync();
        await c.ToggleAsync();

        _recorder.Audio = new short[16000 * 3];
        await c.ToggleAsync();
        await c.ToggleAsync();

        Assert.Equal(0, _transcriber.Calls);
        Assert.Equal(2, _view.Hints.Count(h => h == UserMessages.DidntCatch));
        Assert.Equal(0, _pending.Count);
        Assert.Equal(DictationState.Idle, c.State);
    }

    [Fact]
    public async Task Hotkey_while_transcribing_shows_busy()
    {
        var c = Create();
        _recorder.Audio = Speech(1);
        _transcriber.Gate = new TaskCompletionSource();
        await c.ToggleAsync();
        var stopping = c.ToggleAsync();

        Assert.Equal(DictationState.Transcribing, c.State);
        await c.ToggleAsync();
        Assert.Contains(UserMessages.Busy, _view.Hints);

        _transcriber.Gate.SetResult();
        await stopping;
        Assert.Equal(DictationState.Idle, c.State);
        Assert.Single(_inserter.Inserted);
    }

    [Fact]
    public async Task Cancel_while_recording_discards_audio()
    {
        var c = Create();
        await c.ToggleAsync();
        await c.CancelAsync();

        Assert.Equal(DictationState.Idle, c.State);
        Assert.False(_recorder.IsRecording);
        Assert.Equal(1, _recorder.Cancels);
        Assert.Equal(0, _transcriber.Calls);
        Assert.Empty(_inserter.Inserted);
        Assert.Equal(0, _pending.Count);
    }

    [Fact]
    public async Task Cancel_while_transcribing_keeps_audio_for_retry()
    {
        var c = Create();
        _recorder.Audio = Speech(1);
        _transcriber.Gate = new TaskCompletionSource();
        await c.ToggleAsync();
        var stopping = c.ToggleAsync();

        await c.CancelAsync();
        await stopping;

        Assert.Equal(DictationState.Idle, c.State);
        Assert.Equal(1, _pending.Count);
        Assert.Empty(_inserter.Inserted);
        Assert.Contains(UserMessages.CancelledSaved, _view.Hints);
    }

    [Fact]
    public async Task Failure_keeps_audio_and_shows_friendly_error_then_retry_copies()
    {
        var c = Create();
        _recorder.Audio = Speech(1);
        _transcriber.Error = new TranscriptionException(TranscriptionErrorKind.Network, "down");

        await c.ToggleAsync();
        await c.ToggleAsync();

        Assert.Equal(1, _pending.Count);
        Assert.Equal("Network", _pending.Latest()!.LastError);
        var error = Assert.Single(_view.Errors);
        Assert.Equal(UserAction.Retry, error.Action);
        Assert.Contains("No internet", error.Text, StringComparison.Ordinal);
        Assert.Empty(_history.Entries);

        _transcriber.Error = null;
        _transcriber.Result = "Recovered text";
        await c.RetryLastAsync();

        Assert.Equal(0, _pending.Count);
        Assert.Equal(["Recovered text "], _inserter.Copied);
        Assert.Empty(_inserter.Inserted);
        Assert.Contains(UserMessages.Copied, _view.Hints);
        Assert.Equal("Recovered text", _history.Entries.Single().Text);
    }

    [Fact]
    public async Task Daily_quota_message_shows_reset_time_in_local_zone()
    {
        var c = Create();
        _recorder.Audio = Speech(1);
        _transcriber.Error = new TranscriptionException(TranscriptionErrorKind.DailyQuota, "429", 429);

        await c.ToggleAsync();
        await c.ToggleAsync();

        Assert.Equal("Free daily limit reached. Resets at 12:30 PM IST tomorrow. Your recording is saved.", _view.Errors.Single().Text);
    }

    [Fact]
    public async Task No_key_opens_settings_instead_of_recording()
    {
        _hasKey = false;
        var c = Create();
        await c.ToggleAsync();

        Assert.Equal(DictationState.Idle, c.State);
        Assert.False(_recorder.IsRecording);
        Assert.Equal(UserAction.OpenSettings, _view.Errors.Single().Action);
    }

    [Fact]
    public async Task Microphone_blocked_points_to_privacy_settings()
    {
        _recorder.StartError = new RecorderException(RecorderErrorKind.AccessDenied, "denied");
        var c = Create();
        await c.ToggleAsync();

        Assert.Equal(DictationState.Idle, c.State);
        var error = _view.Errors.Single();
        Assert.Equal(UserAction.OpenMicrophonePrivacySettings, error.Action);
        Assert.Contains("Let desktop apps access your microphone", error.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Warns_30_seconds_before_the_limit_then_auto_stops()
    {
        _settings.Recording.MaxSeconds = 120;
        var c = Create();
        _recorder.Audio = Speech(1);
        await c.ToggleAsync();

        _recorder.Elapsed = TimeSpan.FromSeconds(80);
        await c.TickAsync();
        Assert.Empty(_view.Warnings);

        _recorder.Elapsed = TimeSpan.FromSeconds(91);
        await c.TickAsync();
        await c.TickAsync();
        Assert.Equal([UserMessages.ThirtySecondsLeft], _view.Warnings);

        _recorder.Elapsed = TimeSpan.FromSeconds(120);
        await c.TickAsync();
        Assert.Equal(DictationState.Idle, c.State);
        Assert.Equal(1, _transcriber.Calls);
        Assert.Single(_inserter.Inserted);
    }

    [Fact]
    public async Task Insert_failure_falls_back_to_clipboard_message()
    {
        var c = Create();
        _recorder.Audio = Speech(1);
        _inserter.Outcome = InsertOutcome.CopiedToClipboard;
        await c.ToggleAsync();
        await c.ToggleAsync();
        Assert.Contains(UserMessages.Copied, _view.Hints);
        Assert.Single(_history.Entries);
    }

    [Fact]
    public async Task Empty_transcript_is_didnt_catch_and_audio_is_deleted()
    {
        var c = Create();
        _recorder.Audio = Speech(1);
        _transcriber.Result = "   ";
        await c.ToggleAsync();
        await c.ToggleAsync();

        Assert.Contains(UserMessages.DidntCatch, _view.Hints);
        Assert.Empty(_inserter.Inserted);
        Assert.Equal(0, _pending.Count);
        Assert.Empty(_history.Entries);
    }

    [Fact]
    public async Task History_can_be_disabled()
    {
        _settings.History.Enabled = false;
        var c = Create();
        _recorder.Audio = Speech(1);
        await c.ToggleAsync();
        await c.ToggleAsync();
        Assert.Empty(_history.Entries);
        Assert.Single(_inserter.Inserted);
    }

    [Fact]
    public async Task Unexpected_exception_is_contained_and_microphone_released()
    {
        var c = Create();
        await c.ToggleAsync();
        _recorder.StopError = new InvalidOperationException("driver exploded");

        await c.ToggleAsync();

        Assert.Equal(DictationState.Idle, c.State);
        Assert.Single(_view.Errors);
        Assert.Contains(_log.Lines, l => l.StartsWith("ERROR", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Retry_with_nothing_pending_says_so()
    {
        await Create().RetryLastAsync();
        Assert.Contains(UserMessages.NothingToRetry, _view.Hints);
    }

    [Fact]
    public async Task Logs_never_contain_transcript_text()
    {
        var c = Create();
        _recorder.Audio = Speech(1);
        _transcriber.Result = "my secret dictation";
        await c.ToggleAsync();
        await c.ToggleAsync();
        Assert.DoesNotContain(_log.Lines, l => l.Contains("secret", StringComparison.Ordinal));
    }

    private sealed class FakeRecorder : IAudioRecorder
    {
        public short[] Audio { get; set; } = [];

        public RecorderException? StartError { get; set; }

        public Exception? StopError { get; set; }

        public int Cancels { get; private set; }

        public bool IsRecording { get; private set; }

        public TimeSpan Elapsed { get; set; }

        public float Level => 0.5f;

        public Action<short[]>? SampleSink { get; set; }

        public Task StartAsync(string? deviceId, CancellationToken ct)
        {
            if (StartError is not null)
            {
                throw StartError;
            }

            IsRecording = true;
            Elapsed = TimeSpan.Zero;
            SampleSink?.Invoke(Audio.Length > 0 ? Audio : [1, 2, 3]);
            return Task.CompletedTask;
        }

        public Task<RecordedAudio> StopAsync()
        {
            IsRecording = false;
            if (StopError is not null)
            {
                throw StopError;
            }

            return Task.FromResult(new RecordedAudio(Audio, 16000));
        }

        public Task CancelAsync()
        {
            IsRecording = false;
            Cancels++;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeTranscriber : ITranscriber
    {
        public string Result { get; set; } = "Default text.";

        public TranscriptionException? Error { get; set; }

        public TaskCompletionSource? Gate { get; set; }

        public int Calls { get; private set; }

        public TranscriptionRequest? LastRequest { get; private set; }

        public bool PendingExistedDuringCall { get; private set; }

        public Func<int>? PendingCount { get; set; }

        public int WarmUps { get; private set; }

        public void WarmUp(PlainspokenSettings settings) => WarmUps++;

        public async Task<TranscriptionOutcome> TranscribeAsync(TranscriptionRequest request, PlainspokenSettings settings, IProgress<string>? status, CancellationToken ct)
        {
            Calls++;
            LastRequest = request;
            PendingExistedDuringCall = PendingCount?.Invoke() == 1;
            if (Gate is not null)
            {
                await Gate.Task.WaitAsync(ct);
            }

            if (Error is not null)
            {
                throw Error;
            }

            return new TranscriptionOutcome(Result, EngineKind.Transcribe);
        }
    }

    private sealed class FakeInserter : ITextInserter
    {
        public List<string> Inserted { get; } = [];

        public List<string> Copied { get; } = [];

        public InsertOutcome Outcome { get; set; } = InsertOutcome.Inserted;

        public Task<InsertOutcome> InsertAsync(string text, CancellationToken ct)
        {
            Inserted.Add(text);
            return Task.FromResult(Outcome);
        }

        public Task<bool> CopyAsync(string text)
        {
            Copied.Add(text);
            return Task.FromResult(true);
        }
    }

    private sealed class FakeView : IDictationView
    {
        public List<string> Events { get; } = [];

        public List<DictationState> States { get; } = [];

        public List<string> Hints { get; } = [];

        public List<string> Warnings { get; } = [];

        public List<UserMessage> Errors { get; } = [];

        public void OnStateChanged(DictationState state) => States.Add(state);

        public void ShowRecording() => Events.Add("recording");

        public void ShowTranscribing(string text) => Events.Add("transcribing:" + text);

        public void ShowWarning(string text) => Warnings.Add(text);

        public void ShowHint(string text) => Hints.Add(text);

        public void ShowError(UserMessage message) => Errors.Add(message);

        public void HideOverlay() => Events.Add("hide");
    }

    private sealed class FakeLiveTranscriber : ILiveTranscriber
    {
        public string Result { get; set; } = "Live text.";

        public Exception? Error { get; set; }

        public List<FakeLiveSession> Sessions { get; } = [];

        public bool Available { get; set; } = true;

        public bool CanStart(PlainspokenSettings settings) => Available;

        public ILiveSession Start(PlainspokenSettings settings)
        {
            var s = new FakeLiveSession(this);
            Sessions.Add(s);
            return s;
        }
    }

    private sealed class FakeLiveSession(FakeLiveTranscriber owner) : ILiveSession
    {
        public int Pushed { get; private set; }

        public bool Disposed { get; private set; }

        public bool Finished { get; private set; }

        public void Push(short[] samples) => Pushed += samples.Length;

        public Task<string> FinishAsync(CancellationToken ct)
        {
            Finished = true;
            return owner.Error is not null ? Task.FromException<string>(owner.Error) : Task.FromResult(owner.Result);
        }

        public void Abort()
        {
        }

        public void Dispose() => Disposed = true;
    }

    private sealed class FakeSounds : ISoundPlayer
    {
        public int Starts { get; private set; }

        public int Stops { get; private set; }

        public void PlayStart() => Starts++;

        public void PlayStop() => Stops++;
    }
}
