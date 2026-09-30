using Plainspoken.Core.Audio;
using Plainspoken.Core.Logging;
using Plainspoken.Core.Settings;
using Plainspoken.Core.Storage;
using Plainspoken.Core.Transcription;

namespace Plainspoken.Core.Dictation;

/// <summary>
/// The dictation state machine (SPEC §2): Idle → Recording → Transcribing → Idle.
/// Platform-neutral: the app supplies recorder, inserter, view and sounds.
/// All public members must be called on the UI thread; they never throw.
/// </summary>
public sealed class DictationController
{
    private static readonly TimeSpan WarnBeforeEnd = TimeSpan.FromSeconds(30);

    private readonly Func<PlainspokenSettings> _settings;
    private readonly Func<bool> _hasApiKey;
    private readonly IAudioRecorder _recorder;
    private readonly ITranscriber _transcriber;
    private readonly ITextInserter _inserter;
    private readonly IDictationView _view;
    private readonly ISoundPlayer _sounds;
    private readonly HistoryStore _history;
    private readonly PendingStore _pending;
    private readonly ILog _log;
    private readonly TimeProvider _time;
    private readonly ILiveTranscriber? _liveTranscriber;

    private ILiveSession? _live;
    private bool _starting;
    private bool _stopping;
    private bool _warned;
    private CancellationTokenSource? _transcribeCts;

    public DictationController(
        Func<PlainspokenSettings> settings,
        Func<bool> hasApiKey,
        IAudioRecorder recorder,
        ITranscriber transcriber,
        ITextInserter inserter,
        IDictationView view,
        ISoundPlayer sounds,
        HistoryStore history,
        PendingStore pending,
        ILog log,
        TimeProvider? time = null,
        ILiveTranscriber? liveTranscriber = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _hasApiKey = hasApiKey ?? throw new ArgumentNullException(nameof(hasApiKey));
        _recorder = recorder ?? throw new ArgumentNullException(nameof(recorder));
        _transcriber = transcriber ?? throw new ArgumentNullException(nameof(transcriber));
        _inserter = inserter ?? throw new ArgumentNullException(nameof(inserter));
        _view = view ?? throw new ArgumentNullException(nameof(view));
        _sounds = sounds ?? throw new ArgumentNullException(nameof(sounds));
        _history = history ?? throw new ArgumentNullException(nameof(history));
        _pending = pending ?? throw new ArgumentNullException(nameof(pending));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _time = time ?? TimeProvider.System;
        _liveTranscriber = liveTranscriber;
    }

    public DictationState State { get; private set; } = DictationState.Idle;

    public TimeSpan Elapsed => _recorder.Elapsed;

    public float Level => _recorder.Level;

    /// <summary>Hotkey / tray click / ■ on the pill.</summary>
    public async Task ToggleAsync()
    {
        try
        {
            if (_starting || _stopping)
            {
                return;
            }

            switch (State)
            {
                case DictationState.Idle:
                    await StartAsync();
                    break;
                case DictationState.Recording:
                    await StopAndTranscribeAsync();
                    break;
                default:
                    _view.ShowHint(UserMessages.Busy);
                    break;
            }
        }
        catch (Exception ex)
        {
            await RecoverAsync(ex);
        }
    }

    /// <summary>Esc / ✕. While recording: discard. While transcribing: abort and keep the audio for Retry.</summary>
    public async Task CancelAsync()
    {
        try
        {
            if (State == DictationState.Transcribing)
            {
                _transcribeCts?.Cancel();
                return;
            }

            if (State != DictationState.Recording || _starting || _stopping)
            {
                return;
            }

            _stopping = true;
            try
            {
                await _recorder.CancelAsync();
            }
            finally
            {
                _stopping = false;
                _recorder.SampleSink = null;
                DropLive();
            }

            _log.Info("recording cancelled");
            _view.ShowHint(UserMessages.Cancelled);
            SetState(DictationState.Idle);
        }
        catch (Exception ex)
        {
            await RecoverAsync(ex);
        }
    }

    /// <summary>Call a few times per second from a UI timer: handles the 30-second warning and auto-stop.</summary>
    public Task TickAsync()
    {
        if (State != DictationState.Recording || _starting || _stopping)
        {
            return Task.CompletedTask;
        }

        var max = TimeSpan.FromSeconds(_settings().Recording.MaxSeconds);
        var elapsed = _recorder.Elapsed;
        if (elapsed >= max)
        {
            _log.Info("maximum recording length reached; auto-stopping");
            return ToggleAsync();
        }

        if (!_warned && max > WarnBeforeEnd * 2 && elapsed >= max - WarnBeforeEnd)
        {
            _warned = true;
            _view.ShowWarning(UserMessages.ThirtySecondsLeft);
        }

        return Task.CompletedTask;
    }

    /// <summary>Tray menu "Retry last dictation". Copies the result to the clipboard.</summary>
    public async Task RetryLastAsync()
    {
        try
        {
            if (State != DictationState.Idle || _starting || _stopping)
            {
                _view.ShowHint(UserMessages.Busy);
                return;
            }

            var pending = _pending.Latest();
            if (pending is null)
            {
                _view.ShowHint(UserMessages.NothingToRetry);
                return;
            }

            if (!_hasApiKey())
            {
                _view.ShowError(UserMessages.NoKey);
                return;
            }

            var wav = _pending.ReadAudio(pending);
            var duration = pending.DurationSeconds > 0
                ? pending.DurationSeconds
                : WavEncoder.DurationSeconds((wav.Length - WavEncoder.HeaderSize) / 2);
            _log.Info($"retry: pending recording {duration:0.0}s, attempt {pending.Attempts + 1}");
            SetState(DictationState.Transcribing);
            _view.ShowTranscribing("Retrying…");
            await TranscribeAndDeliverAsync(wav, duration, pending, insertAtCursor: false);
        }
        catch (Exception ex)
        {
            await RecoverAsync(ex);
        }
    }

    private async Task StartAsync()
    {
        if (!_hasApiKey())
        {
            _view.ShowError(UserMessages.NoKey);
            return;
        }

        var settings = _settings();
        _starting = true;
        StartLive(settings);
        _transcriber.WarmUp(settings);
        try
        {
            await _recorder.StartAsync(settings.Local.MicrophoneId, CancellationToken.None);
        }
        catch (RecorderException ex)
        {
            _log.Warn($"microphone start failed: {ex.Kind} ({ex.InnerException?.GetType().Name})");
            _recorder.SampleSink = null;
            DropLive();
            _view.ShowError(UserMessages.For(ex.Kind));
            return;
        }
        finally
        {
            _starting = false;
        }

        _warned = false;
        SetState(DictationState.Recording);
        if (settings.Recording.Sounds)
        {
            _sounds.PlayStart();
        }

        _view.ShowRecording();
        _log.Info("recording started");
    }

    private async Task StopAndTranscribeAsync()
    {
        var settings = _settings();
        SetState(DictationState.Transcribing);
        _view.ShowTranscribing("Transcribing…");
        if (settings.Recording.Sounds)
        {
            _sounds.PlayStop();
        }

        RecordedAudio audio;
        _stopping = true;
        var live = _live;
        _live = null;
        try
        {
            audio = await _recorder.StopAsync();
        }
        catch (Exception ex)
        {
            _log.Error("microphone stop failed", ex);
            live?.Dispose();
            _view.ShowError(UserMessages.For(RecorderErrorKind.Failed));
            SetState(DictationState.Idle);
            return;
        }
        finally
        {
            _stopping = false;
            _recorder.SampleSink = null;
        }

        var verdict = ClipAnalyzer.Analyze(audio.Samples, audio.SampleRate);
        _log.Info($"recording stopped: {audio.DurationSeconds:0.0}s, clip {verdict}");
        if (verdict != ClipVerdict.Ok)
        {
            live?.Dispose();
            _view.ShowHint(UserMessages.DidntCatch);
            SetState(DictationState.Idle);
            return;
        }

        var wav = WavEncoder.Encode(audio.Samples, audio.SampleRate);
        PendingRecording? pending = null;
        try
        {
            pending = _pending.Save(wav, audio.DurationSeconds, _time.GetUtcNow());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Error("could not save the recording for retry; continuing", ex);
        }

        await TranscribeAndDeliverAsync(wav, audio.DurationSeconds, pending, insertAtCursor: true, live);
    }

    private async Task TranscribeAndDeliverAsync(byte[] wav, double duration, PendingRecording? pending, bool insertAtCursor,
        ILiveSession? live = null)
    {
        var settings = _settings();
        using var cts = new CancellationTokenSource();
        _transcribeCts = cts;
        var progress = new Progress<string>(s =>
        {
            if (State == DictationState.Transcribing)
            {
                _view.ShowTranscribing(s);
            }
        });

        string text;
        try
        {
            var raw = live is null ? null : await TryLiveAsync(live, cts.Token);
            if (raw is null)
            {
                var outcome = await _transcriber.TranscribeAsync(TranscriptionRequest.From(wav, duration, settings), settings, progress, cts.Token);
                raw = outcome.Text;
            }

            text = TextPostProcessor.Clean(raw);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            _log.Info("transcription cancelled by user");
            if (pending is not null)
            {
                _pending.MarkFailed(pending, "Cancelled");
            }

            _view.ShowHint(pending is null ? UserMessages.Cancelled : UserMessages.CancelledSaved);
            SetState(DictationState.Idle);
            return;
        }
        catch (TranscriptionException ex)
        {
            _log.Warn($"transcription failed: {ex.Kind} — {ex.Message}");
            if (pending is not null)
            {
                _pending.MarkFailed(pending, ex.Kind.ToString());
            }

            _view.ShowError(UserMessages.For(ex, _time.GetUtcNow(), _time.LocalTimeZone));
            SetState(DictationState.Idle);
            return;
        }
        catch (Exception ex)
        {
            _log.Error("transcription crashed", ex);
            if (pending is not null)
            {
                _pending.MarkFailed(pending, "Unexpected");
            }

            _view.ShowError(UserMessages.Unexpected);
            SetState(DictationState.Idle);
            return;
        }
        finally
        {
            _transcribeCts = null;
        }

        // We have a transcript: the audio is no longer needed.
        if (pending is not null)
        {
            _pending.Delete(pending);
        }

        if (text.Length == 0)
        {
            _view.ShowHint(UserMessages.DidntCatch);
            SetState(DictationState.Idle);
            return;
        }

        if (settings.History.Enabled)
        {
            _history.Add(text, duration, _time.GetUtcNow());
        }

        var toInsert = TextPostProcessor.ForInsertion(text, settings.Insertion.TrailingSpace);
        InsertOutcome result;
        if (insertAtCursor)
        {
            _view.HideOverlay();
            result = await _inserter.InsertAsync(toInsert, CancellationToken.None);
        }
        else
        {
            result = await _inserter.CopyAsync(toInsert) ? InsertOutcome.CopiedToClipboard : InsertOutcome.Failed;
        }

        _log.Info($"delivered: {result}, {text.Length} chars");
        switch (result)
        {
            case InsertOutcome.Inserted:
                _view.HideOverlay();
                break;
            case InsertOutcome.CopiedToClipboard:
                _view.ShowHint(UserMessages.Copied);
                break;
            default:
                _view.ShowError(UserMessages.InsertFailed);
                break;
        }

        SetState(DictationState.Idle);
    }

    /// <summary>Starts a live (streaming) session if enabled. Any failure just means no live session.</summary>
    private void StartLive(PlainspokenSettings settings)
    {
        DropLive();
        if (!settings.Transcription.LiveStreaming || _liveTranscriber is null || !_liveTranscriber.CanStart(settings))
        {
            return;
        }

        try
        {
            var live = _liveTranscriber.Start(settings);
            _recorder.SampleSink = live.Push;
            _live = live;
        }
        catch (Exception ex)
        {
            _log.Warn($"live: could not start ({ex.GetType().Name}); using the normal engine");
            _recorder.SampleSink = null;
        }
    }

    private void DropLive()
    {
        var live = _live;
        _live = null;
        live?.Dispose();
    }

    /// <summary>The live transcript, or null to fall back to the normal engine. User cancellation propagates.</summary>
    private async Task<string?> TryLiveAsync(ILiveSession live, CancellationToken ct)
    {
        try
        {
            var text = await live.FinishAsync(ct);
            if (!string.IsNullOrWhiteSpace(text))
            {
                return text;
            }

            _log.Warn("live: no text returned; using the normal engine");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var kind = ex is TranscriptionException te ? te.Kind.ToString() : ex.GetType().Name;
            _log.Warn($"live: failed ({kind}: {Logging.Redactor.Clean(ex.Message, 200)}); using the normal engine");
        }
        finally
        {
            live.Dispose();
        }

        return null;
    }

    private async Task RecoverAsync(Exception ex)
    {
        _log.Error("dictation controller error", ex);
        _starting = false;
        _stopping = false;
        _recorder.SampleSink = null;
        DropLive();
        try
        {
            if (_recorder.IsRecording)
            {
                await _recorder.CancelAsync();
            }
        }
        catch (Exception inner)
        {
            _log.Error("could not release the microphone", inner);
        }

        _view.ShowError(new UserMessage("Something went wrong. Please try again."));
        SetState(DictationState.Idle);
    }

    private void SetState(DictationState state)
    {
        if (State == state)
        {
            return;
        }

        State = state;
        _view.OnStateChanged(state);
    }
}
