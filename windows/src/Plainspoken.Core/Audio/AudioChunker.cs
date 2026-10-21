namespace Plainspoken.Core.Audio;

/// <summary>Audio in a compressed format (AAC in an MP4 container), or the WAV itself.</summary>
public sealed record EncodedAudio(byte[] Bytes, string MimeType, string FileName)
{
    public static EncodedAudio FromWav(byte[] wav) => new(wav, "audio/wav", "audio.wav");
}

/// <summary>Turns 16 kHz mono PCM16 into <see cref="EncodedAudio"/>, or null if this computer can't. Implemented per platform.</summary>
public interface IAudioCompressor
{
    EncodedAudio? Compress(short[] samples, int sampleRate);
}

/// <summary>Splits long recordings for services that only accept short requests (Sarvam: 30 s), cutting at quiet moments.</summary>
public static class AudioChunker
{
    private const double WindowSeconds = 0.1;

    /// <summary>
    /// Pieces of at most <paramref name="maxSeconds"/>. Each cut is placed at the quietest 100 ms within the last
    /// <paramref name="searchSeconds"/> before the limit, so words aren't split when the speaker pauses.
    /// </summary>
    public static IReadOnlyList<short[]> Split(short[] samples, int sampleRate, double maxSeconds, double searchSeconds = 5.0)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxSeconds, 1);
        var max = (int)(maxSeconds * sampleRate);
        if (samples.Length <= max)
        {
            return [samples];
        }

        var window = Math.Max(1, (int)(WindowSeconds * sampleRate));
        var search = Math.Min(max - window, (int)(searchSeconds * sampleRate));
        var pieces = new List<short[]>();
        var start = 0;
        while (samples.Length - start > max)
        {
            var cut = start + max;
            var quietest = double.MaxValue;
            for (var pos = start + max - search; pos + window <= start + max; pos += Math.Max(1, window / 2))
            {
                var level = ClipAnalyzer.Rms(samples.AsSpan(pos, window));
                if (level < quietest)
                {
                    quietest = level;
                    cut = pos + (window / 2);
                }
            }

            pieces.Add(samples[start..cut]);
            start = cut;
        }

        pieces.Add(samples[start..]);
        return pieces;
    }
}
