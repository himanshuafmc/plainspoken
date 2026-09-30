using System.Buffers.Binary;

namespace Plainspoken.Core.Audio;

public enum SampleEncoding
{
    Pcm16,
    Pcm24,
    Pcm32,
    IeeeFloat,
}

/// <summary>Describes interleaved audio coming from a capture device.</summary>
public sealed record SampleFormat(int SampleRate, int Channels, SampleEncoding Encoding)
{
    public int BytesPerSample => Encoding switch
    {
        SampleEncoding.Pcm16 => 2,
        SampleEncoding.Pcm24 => 3,
        _ => 4,
    };

    public int BlockAlign => BytesPerSample * Channels;

    /// <summary>
    /// Decodes interleaved frames and averages channels into mono floats in [-1, 1].
    /// Returns the number of mono samples written to <paramref name="destination"/>.
    /// </summary>
    public int DecodeToMono(ReadOnlySpan<byte> source, Span<float> destination)
    {
        var frames = Math.Min(source.Length / BlockAlign, destination.Length);
        var bps = BytesPerSample;
        for (var f = 0; f < frames; f++)
        {
            var frame = source.Slice(f * BlockAlign, BlockAlign);
            float sum = 0;
            for (var c = 0; c < Channels; c++)
            {
                sum += ReadSample(frame.Slice(c * bps, bps));
            }

            destination[f] = sum / Channels;
        }

        return frames;
    }

    private float ReadSample(ReadOnlySpan<byte> b) => Encoding switch
    {
        SampleEncoding.Pcm16 => BinaryPrimitives.ReadInt16LittleEndian(b) / 32768f,
        SampleEncoding.Pcm24 => ((b[2] << 24) | (b[1] << 16) | (b[0] << 8)) / 2147483648f,
        SampleEncoding.Pcm32 => BinaryPrimitives.ReadInt32LittleEndian(b) / 2147483648f,
        _ => BinaryPrimitives.ReadSingleLittleEndian(b),
    };
}
