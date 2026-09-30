namespace Plainspoken.Core.Audio;

public enum ClipVerdict
{
    Ok,
    TooShort,
    Silent,
}

/// <summary>Decides whether a recording is worth sending (avoids wasting free-tier requests).</summary>
public static class ClipAnalyzer
{
    public const double MinSeconds = 0.6;
    public const double FrameSeconds = 0.030;

    /// <summary>≈ −45 dBFS. Laptop-mic speech is typically −30…−20 dBFS; room noise −60…−50.</summary>
    public const double RmsThreshold = 0.0056;

    public const int MinLoudFrames = 3;

    public static ClipVerdict Analyze(ReadOnlySpan<short> samples, int sampleRate = WavEncoder.TargetSampleRate)
    {
        if (sampleRate <= 0 || samples.Length < MinSeconds * sampleRate)
        {
            return ClipVerdict.TooShort;
        }

        return CountLoudFrames(samples, sampleRate) >= MinLoudFrames ? ClipVerdict.Ok : ClipVerdict.Silent;
    }

    public static int CountLoudFrames(ReadOnlySpan<short> samples, int sampleRate)
    {
        var frame = Math.Max(1, (int)(sampleRate * FrameSeconds));
        var loud = 0;
        for (var start = 0; start + frame <= samples.Length; start += frame)
        {
            if (Rms(samples.Slice(start, frame)) >= RmsThreshold)
            {
                loud++;
            }
        }

        return loud;
    }

    public static double Rms(ReadOnlySpan<short> samples)
    {
        if (samples.IsEmpty)
        {
            return 0;
        }

        double sum = 0;
        foreach (var s in samples)
        {
            var v = s / 32768.0;
            sum += v * v;
        }

        return Math.Sqrt(sum / samples.Length);
    }
}
