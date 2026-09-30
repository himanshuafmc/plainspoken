namespace Plainspoken.Core.Audio;

/// <summary>Builds the short, soft start/stop sounds as in-memory WAV files (no asset files needed).</summary>
public static class ToneGenerator
{
    private const int Rate = 22_050;

    /// <summary>Rising two-note chirp.</summary>
    public static byte[] StartSound() => Notes((660, 0.07), (880, 0.09));

    /// <summary>Falling two-note chirp.</summary>
    public static byte[] StopSound() => Notes((880, 0.07), (587, 0.09));

    public static byte[] Notes(params (double Hz, double Seconds)[] notes)
    {
        ArgumentNullException.ThrowIfNull(notes);
        var samples = new List<short>();
        foreach (var (hz, seconds) in notes)
        {
            var n = (int)(seconds * Rate);
            var fade = Math.Max(1, (int)(0.012 * Rate));
            for (var i = 0; i < n; i++)
            {
                var env = Math.Min(1.0, Math.Min(i, n - 1 - i) / (double)fade);
                var v = Math.Sin(2 * Math.PI * hz * i / Rate) * env * 0.18; // quiet
                samples.Add((short)(v * short.MaxValue));
            }
        }

        return WavEncoder.Encode(samples.ToArray(), Rate);
    }
}
