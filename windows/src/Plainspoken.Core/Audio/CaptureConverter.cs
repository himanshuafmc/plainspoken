namespace Plainspoken.Core.Audio;

/// <summary>
/// Turns raw capture-device buffers into 16 kHz mono PCM16, and tracks a level for the meter.
/// <see cref="Push"/> is called on the audio thread; <see cref="Level"/> may be read from any
/// thread; <see cref="Finish"/> is called once after capture has stopped.
/// </summary>
public sealed class CaptureConverter
{
    private readonly SampleFormat _format;
    private readonly StreamingResampler _resampler;
    private readonly List<float> _resampled = new();
    private readonly object _gate = new();
    private float[] _mono = new float[4096];
    private short[] _samples = new short[WavEncoder.TargetSampleRate * 30];
    private int _count;
    private volatile float _level;

    public CaptureConverter(SampleFormat format)
    {
        _format = format ?? throw new ArgumentNullException(nameof(format));
        _resampler = new StreamingResampler(format.SampleRate);
    }

    /// <summary>Called (inside Push, on the audio thread) with each block of new 16 kHz samples.</summary>
    public Action<short[]>? SamplesAvailable { get; set; }

    /// <summary>Recent peak level, 0..1, suitable for a simple meter.</summary>
    public float Level => _level;

    /// <summary>Number of 16 kHz samples collected so far.</summary>
    public int SampleCount
    {
        get
        {
            lock (_gate)
            {
                return _count;
            }
        }
    }

    public void Push(ReadOnlySpan<byte> buffer)
    {
        var frames = buffer.Length / _format.BlockAlign;
        if (frames == 0)
        {
            return;
        }

        lock (_gate)
        {
            if (_mono.Length < frames)
            {
                _mono = new float[frames];
            }

            var n = _format.DecodeToMono(buffer, _mono);
            float peak = 0;
            for (var i = 0; i < n; i++)
            {
                peak = Math.Max(peak, Math.Abs(_mono[i]));
            }

            // Fast attack, slow release, so the meter looks alive without flicker.
            _level = Math.Max(peak, _level * 0.85f);

            _resampled.Clear();
            _resampler.Process(_mono.AsSpan(0, n), _resampled);
            Append(_resampled);
        }
    }

    public short[] Finish()
    {
        lock (_gate)
        {
            _resampled.Clear();
            _resampler.Flush(_resampled);
            Append(_resampled);
            _level = 0;
            return _samples.AsSpan(0, _count).ToArray();
        }
    }

    private void Append(List<float> values)
    {
        if (values.Count == 0)
        {
            return;
        }

        if (_count + values.Count > _samples.Length)
        {
            Array.Resize(ref _samples, Math.Max(_samples.Length * 2, _count + values.Count));
        }

        var start = _count;
        foreach (var v in values)
        {
            var clamped = Math.Clamp(v, -1f, 1f);
            _samples[_count++] = (short)Math.Round(clamped * 32767f);
        }

        SamplesAvailable?.Invoke(_samples.AsSpan(start, values.Count).ToArray());
    }
}
