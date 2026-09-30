using System.Buffers.Binary;

namespace Plainspoken.Core.Audio;

/// <summary>
/// Writes and reads canonical 44-byte-header WAV files holding mono 16-bit PCM.
/// This is the only format Plainspoken sends to Gemini (16 kHz mono PCM16, ~1.9 MB/min).
/// </summary>
public static class WavEncoder
{
    public const int HeaderSize = 44;
    public const int TargetSampleRate = 16_000;

    public static byte[] Encode(ReadOnlySpan<short> samples, int sampleRate = TargetSampleRate)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        const short channels = 1;
        const short bitsPerSample = 16;
        const short blockAlign = channels * bitsPerSample / 8;
        var dataBytes = checked(samples.Length * blockAlign);
        var wav = new byte[HeaderSize + dataBytes];
        var s = wav.AsSpan();

        "RIFF"u8.CopyTo(s);
        BinaryPrimitives.WriteInt32LittleEndian(s[4..], 36 + dataBytes);
        "WAVE"u8.CopyTo(s[8..]);
        "fmt "u8.CopyTo(s[12..]);
        BinaryPrimitives.WriteInt32LittleEndian(s[16..], 16);               // fmt chunk size
        BinaryPrimitives.WriteInt16LittleEndian(s[20..], 1);                // PCM
        BinaryPrimitives.WriteInt16LittleEndian(s[22..], channels);
        BinaryPrimitives.WriteInt32LittleEndian(s[24..], sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(s[28..], sampleRate * blockAlign); // byte rate
        BinaryPrimitives.WriteInt16LittleEndian(s[32..], blockAlign);
        BinaryPrimitives.WriteInt16LittleEndian(s[34..], bitsPerSample);
        "data"u8.CopyTo(s[36..]);
        BinaryPrimitives.WriteInt32LittleEndian(s[40..], dataBytes);

        var data = s[HeaderSize..];
        for (var i = 0; i < samples.Length; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(data[(i * 2)..], samples[i]);
        }

        return wav;
    }

    /// <summary>
    /// Reads a mono PCM16 WAV (as written by <see cref="Encode"/>). Walks the chunk list so
    /// files with extra chunks (e.g. LIST) also work.
    /// </summary>
    public static (short[] Samples, int SampleRate) Decode(ReadOnlySpan<byte> wav)
    {
        if (wav.Length < 12 || !wav[..4].SequenceEqual("RIFF"u8) || !wav[8..12].SequenceEqual("WAVE"u8))
        {
            throw new FormatException("Not a RIFF/WAVE file.");
        }

        int sampleRate = 0, channels = 0, bits = 0, format = 0;
        var pos = 12;
        while (pos + 8 <= wav.Length)
        {
            var id = wav.Slice(pos, 4);
            var size = BinaryPrimitives.ReadInt32LittleEndian(wav[(pos + 4)..]);
            var body = pos + 8;
            if (size < 0 || body + size > wav.Length)
            {
                size = wav.Length - body; // tolerate a truncated last chunk
            }

            if (id.SequenceEqual("fmt "u8) && size >= 16)
            {
                format = BinaryPrimitives.ReadInt16LittleEndian(wav[body..]);
                channels = BinaryPrimitives.ReadInt16LittleEndian(wav[(body + 2)..]);
                sampleRate = BinaryPrimitives.ReadInt32LittleEndian(wav[(body + 4)..]);
                bits = BinaryPrimitives.ReadInt16LittleEndian(wav[(body + 14)..]);
            }
            else if (id.SequenceEqual("data"u8))
            {
                if (format != 1 || channels != 1 || bits != 16)
                {
                    throw new FormatException($"Expected mono PCM16, got format={format} channels={channels} bits={bits}.");
                }

                var count = size / 2;
                var samples = new short[count];
                for (var i = 0; i < count; i++)
                {
                    samples[i] = BinaryPrimitives.ReadInt16LittleEndian(wav[(body + (i * 2))..]);
                }

                return (samples, sampleRate);
            }

            pos = body + size + (size & 1); // chunks are word-aligned
        }

        throw new FormatException("WAV has no data chunk.");
    }

    public static double DurationSeconds(int sampleCount, int sampleRate = TargetSampleRate) =>
        sampleRate <= 0 ? 0 : (double)sampleCount / sampleRate;
}
