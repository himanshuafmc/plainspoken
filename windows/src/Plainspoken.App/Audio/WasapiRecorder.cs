using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Plainspoken.App.Platform;
using Plainspoken.Core.Audio;
using Plainspoken.Core.Dictation;
using Plainspoken.Core.Logging;

namespace Plainspoken.App.Audio;

/// <summary>
/// Microphone capture with NAudio WASAPI (shared mode), converted on the fly to 16 kHz mono PCM16.
/// Threading: Start/Stop/Cancel are called on the UI thread; NAudio calls <see cref="OnData"/> on its
/// own capture thread, which only touches the thread-safe <see cref="CaptureConverter"/> (never the UI).
/// The capture objects are created on a thread-pool thread, so NAudio has no UI SynchronizationContext
/// and raises RecordingStopped on its own thread; stop is awaited with a timeout, never blocked on.
/// </summary>
internal sealed class WasapiRecorder : IAudioRecorder, IDisposable
{
    private const int E_ACCESSDENIED = unchecked((int)0x80070005);
    private const int E_NOTFOUND = unchecked((int)0x80070490);
    private const int AUDCLNT_E_DEVICE_INVALIDATED = unchecked((int)0x88890004);
    private const int AUDCLNT_E_DEVICE_IN_USE = unchecked((int)0x8889000A);
    private const int AUDCLNT_E_CPUUSAGE_EXCEEDED = unchecked((int)0x88890017);
    private static readonly Guid SubtypePcm = new("00000001-0000-0010-8000-00aa00389b71");
    private static readonly Guid SubtypeFloat = new("00000003-0000-0010-8000-00aa00389b71");
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(3);

    private readonly ILog _log;
    private readonly Stopwatch _clock = new();
    private WasapiCapture? _capture;
    private MMDevice? _device;
    private CaptureConverter? _converter;
    private TaskCompletionSource? _stopped;
    private volatile bool _recording;
    private volatile Action<short[]>? _sampleSink;

    public WasapiRecorder(ILog log) => _log = log;

    /// <summary>Raised on the capture thread if the device stops by itself (e.g. unplugged).</summary>
    public event EventHandler? CaptureFaulted;

    public bool IsRecording => _recording;

    public TimeSpan Elapsed => _clock.Elapsed;

    public float Level => _converter?.Level ?? 0f;

    /// <summary>Receives converted 16 kHz blocks on the capture thread (live streaming). Must not block.</summary>
    public Action<short[]>? SampleSink
    {
        get => _sampleSink;
        set => _sampleSink = value;
    }

    public async Task StartAsync(string? deviceId, CancellationToken ct)
    {
        if (_recording || _capture is not null)
        {
            throw new InvalidOperationException("Already recording.");
        }

        if (MicrophonePrivacy.IsBlockedForDesktopApps())
        {
            throw new RecorderException(RecorderErrorKind.AccessDenied, "Microphone privacy switch is off.");
        }

        await Task.Run(() => Open(deviceId), ct).ConfigureAwait(true);
        _clock.Restart();
        _recording = true;
    }

    public async Task<RecordedAudio> StopAsync()
    {
        var converter = _converter;
        await ShutdownAsync().ConfigureAwait(true);
        var samples = converter?.Finish() ?? [];
        return new RecordedAudio(samples, WavEncoder.TargetSampleRate);
    }

    public Task CancelAsync() => ShutdownAsync();

    /// <summary>Lists capture devices as (id, friendly name). Empty on failure.</summary>
    public static IReadOnlyList<(string Id, string Name)> ListDevices()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var list = new List<(string, string)>();
            foreach (var d in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            {
                using (d)
                {
                    list.Add((d.ID, d.FriendlyName));
                }
            }

            return list;
        }
        catch (COMException)
        {
            return [];
        }
    }

    public void Dispose()
    {
        _recording = false;
        ReleaseDevice();
    }

    private void Open(string? deviceId)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            _device = FindDevice(enumerator, deviceId) ?? enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);
            _capture = new WasapiCapture(_device, true, 50) { ShareMode = AudioClientShareMode.Shared };
            var format = ToSampleFormat(_capture.WaveFormat);
            _converter = new CaptureConverter(format) { SamplesAvailable = OnSamples };
            _stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _capture.DataAvailable += OnData;
            _capture.RecordingStopped += OnStopped;
            _capture.StartRecording();
            _log.Info($"microphone opened: {format.SampleRate} Hz, {format.Channels} ch, {format.Encoding}");
        }
        catch (Exception ex)
        {
            ReleaseDevice();
            throw Map(ex);
        }
    }

    private MMDevice? FindDevice(MMDeviceEnumerator enumerator, string? id)
    {
        if (string.IsNullOrEmpty(id))
        {
            return null;
        }

        try
        {
            var d = enumerator.GetDevice(id);
            if (d.State == DeviceState.Active)
            {
                return d;
            }

            d.Dispose();
        }
        catch (COMException)
        {
            // fall through to the default device
        }

        _log.Warn("selected microphone not available; using the default");
        return null;
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        // Capture thread: no UI calls here.
        try
        {
            if (_recording)
            {
                _converter?.Push(e.Buffer.AsSpan(0, e.BytesRecorded));
            }
        }
        catch (Exception ex)
        {
            _log.Error("audio conversion failed", ex);
        }
    }

    private void OnSamples(short[] samples)
    {
        // Capture thread: hand the block to the live session (a non-blocking queue write).
        try
        {
            _sampleSink?.Invoke(samples);
        }
        catch (Exception ex)
        {
            _log.Error("live sample sink failed", ex);
            _sampleSink = null;
        }
    }

    private void OnStopped(object? sender, StoppedEventArgs e)
    {
        // Capture thread (no SynchronizationContext was captured).
        if (e.Exception is not null)
        {
            _log.Error("microphone stopped with an error", e.Exception);
        }

        var unexpected = _recording;
        _stopped?.TrySetResult();
        if (unexpected)
        {
            _log.Warn("microphone stopped unexpectedly");
            try
            {
                CaptureFaulted?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                _log.Error("capture-faulted handler failed", ex);
            }
        }
    }

    private async Task ShutdownAsync()
    {
        _recording = false;
        _clock.Stop();
        var capture = _capture;
        var stopped = _stopped;
        if (capture is null)
        {
            return;
        }

        try
        {
            capture.StopRecording();
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            _log.Warn($"StopRecording: {ex.GetType().Name}");
        }

        if (stopped is not null && await Task.WhenAny(stopped.Task, Task.Delay(StopTimeout)).ConfigureAwait(true) != stopped.Task)
        {
            _log.Warn("microphone did not confirm stop in time; releasing anyway");
        }

        // Dispose joins NAudio's capture thread; do it off the UI thread and don't wait forever.
        var release = Task.Run(ReleaseDevice);
        if (await Task.WhenAny(release, Task.Delay(StopTimeout)).ConfigureAwait(true) != release)
        {
            _log.Warn("microphone release is taking long; continuing");
        }

        _log.Info("microphone released");
    }

    private void ReleaseDevice()
    {
        var capture = Interlocked.Exchange(ref _capture, null);
        if (capture is not null)
        {
            capture.DataAvailable -= OnData;
            capture.RecordingStopped -= OnStopped;
            try
            {
                capture.Dispose();
            }
            catch (Exception ex) when (ex is COMException or InvalidOperationException)
            {
                _log.Warn($"capture dispose: {ex.GetType().Name}");
            }
        }

        Interlocked.Exchange(ref _device, null)?.Dispose();
    }

    private static SampleFormat ToSampleFormat(WaveFormat f)
    {
        var encoding = f.Encoding;
        if (f is WaveFormatExtensible ext)
        {
            encoding = ext.SubFormat == SubtypeFloat ? WaveFormatEncoding.IeeeFloat
                : ext.SubFormat == SubtypePcm ? WaveFormatEncoding.Pcm
                : WaveFormatEncoding.Unknown;
        }

        var sample = (encoding, f.BitsPerSample) switch
        {
            (WaveFormatEncoding.IeeeFloat, 32) => SampleEncoding.IeeeFloat,
            (WaveFormatEncoding.Pcm, 16) => SampleEncoding.Pcm16,
            (WaveFormatEncoding.Pcm, 24) => SampleEncoding.Pcm24,
            (WaveFormatEncoding.Pcm, 32) => SampleEncoding.Pcm32,
            _ => throw new RecorderException(RecorderErrorKind.Failed, $"Unsupported microphone format {f.Encoding}/{f.BitsPerSample}"),
        };
        return new SampleFormat(f.SampleRate, f.Channels, sample);
    }

    private static RecorderException Map(Exception ex) => ex switch
    {
        RecorderException r => r,
        COMException { HResult: E_ACCESSDENIED } or UnauthorizedAccessException =>
            new RecorderException(RecorderErrorKind.AccessDenied, "Access denied", ex),
        COMException { HResult: E_NOTFOUND or AUDCLNT_E_DEVICE_INVALIDATED } =>
            new RecorderException(RecorderErrorKind.NoDevice, "No capture device", ex),
        COMException { HResult: AUDCLNT_E_DEVICE_IN_USE or AUDCLNT_E_CPUUSAGE_EXCEEDED } =>
            new RecorderException(RecorderErrorKind.DeviceBusy, "Device busy", ex),
        _ => new RecorderException(RecorderErrorKind.Failed, ex.GetType().Name, ex),
    };
}
