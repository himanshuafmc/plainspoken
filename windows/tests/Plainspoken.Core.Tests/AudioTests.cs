using System.Buffers.Binary;
using System.Text;
using Plainspoken.Core.Audio;

namespace Plainspoken.Core.Tests;

public class WavEncoderTests
{
    [Fact]
    public void Header_is_canonical_16k_mono_pcm16()
    {
        var wav = WavEncoder.Encode(new short[] { 0, 1, -1, short.MaxValue, short.MinValue });

        Assert.Equal(44 + 10, wav.Length);
        Assert.Equal("RIFF", Encoding.ASCII.GetString(wav, 0, 4));
        Assert.Equal(36 + 10, BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(4)));
        Assert.Equal("WAVEfmt ", Encoding.ASCII.GetString(wav, 8, 8));
        Assert.Equal(16, BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(16)));
        Assert.Equal(1, BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(20)));      // PCM
        Assert.Equal(1, BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(22)));      // mono
        Assert.Equal(16000, BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(24)));
        Assert.Equal(32000, BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(28)));  // byte rate
        Assert.Equal(2, BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(32)));      // block align
        Assert.Equal(16, BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(34)));     // bits
        Assert.Equal("data", Encoding.ASCII.GetString(wav, 36, 4));
        Assert.Equal(10, BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(40)));
    }

    [Fact]
    public void Samples_are_little_endian()
    {
        var wav = WavEncoder.Encode(new short[] { 0x1234, -2 });
        Assert.Equal(new byte[] { 0x34, 0x12, 0xFE, 0xFF }, wav[44..]);
    }

    [Fact]
    public void Round_trips()
    {
        var samples = Enumerable.Range(0, 1000).Select(i => (short)((i * 37) - 18000)).ToArray();
        var (decoded, rate) = WavEncoder.Decode(WavEncoder.Encode(samples));
        Assert.Equal(16000, rate);
        Assert.Equal(samples, decoded);
    }

    [Fact]
    public void One_minute_is_about_1_9_MB()
    {
        var wav = WavEncoder.Encode(new short[16000 * 60]);
        Assert.InRange(wav.Length / 1024.0 / 1024.0, 1.8, 1.9);
    }

    [Fact]
    public void Decode_rejects_non_wav() =>
        Assert.Throws<FormatException>(() => WavEncoder.Decode(new byte[64]));
}

public class ResamplerTests
{
    private static float[] Sine(double hz, int rate, double seconds, float amp = 0.5f) =>
        Enumerable.Range(0, (int)(rate * seconds)).Select(i => (float)(amp * Math.Sin(2 * Math.PI * hz * i / rate))).ToArray();

    private static List<float> Run(float[] input, int inRate, int chunk = 480)
    {
        var r = new StreamingResampler(inRate);
        var output = new List<float>();
        for (var i = 0; i < input.Length; i += chunk)
        {
            r.Process(input.AsSpan(i, Math.Min(chunk, input.Length - i)), output);
        }

        r.Flush(output);
        return output;
    }

    private static double Rms(IEnumerable<float> x)
    {
        var a = x.ToArray();
        return Math.Sqrt(a.Select(v => (double)v * v).Average());
    }

    [Theory]
    [InlineData(48000)]
    [InlineData(44100)]
    [InlineData(32000)]
    [InlineData(22050)]
    public void Output_length_matches_ratio(int inRate)
    {
        var output = Run(Sine(440, inRate, 1.0), inRate);
        Assert.InRange(output.Count, 15990, 16040);
    }

    [Fact]
    public void Speech_band_tone_keeps_its_level_and_frequency()
    {
        var output = Run(Sine(1000, 48000, 1.0), 48000);
        var steady = output.Skip(800).Take(14000).ToList();
        Assert.InRange(Rms(steady), 0.5 / Math.Sqrt(2) * 0.95, 0.5 / Math.Sqrt(2) * 1.05);

        // 1 kHz at 16 kHz → 2 zero crossings per 16 samples.
        var crossings = 0;
        for (var i = 1; i < steady.Count; i++)
        {
            if (Math.Sign(steady[i - 1]) != Math.Sign(steady[i]))
            {
                crossings++;
            }
        }

        Assert.InRange(crossings / (steady.Count / 16000.0), 1990, 2010);
    }

    [Fact]
    public void Tone_above_new_nyquist_is_filtered_out_not_aliased()
    {
        var output = Run(Sine(12000, 48000, 1.0), 48000);
        Assert.True(Rms(output.Skip(800).Take(14000)) < 0.01, "12 kHz should be removed when resampling to 16 kHz");
    }

    [Fact]
    public void Same_rate_passes_through()
    {
        var input = Sine(440, 16000, 0.1);
        Assert.Equal(input, Run(input, 16000));
    }
}

public class CaptureConverterTests
{
    [Fact]
    public void Float_stereo_48k_becomes_16k_mono_pcm16()
    {
        var format = new SampleFormat(48000, 2, SampleEncoding.IeeeFloat);
        var converter = new CaptureConverter(format);
        var frames = 48000;
        var bytes = new byte[frames * format.BlockAlign];
        for (var i = 0; i < frames; i++)
        {
            var v = (float)(0.5 * Math.Sin(2 * Math.PI * 500 * i / 48000));
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * 8), v);
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan((i * 8) + 4), v);
        }

        for (var off = 0; off < bytes.Length; off += 3840)
        {
            converter.Push(bytes.AsSpan(off, Math.Min(3840, bytes.Length - off)));
        }

        Assert.True(converter.Level > 0.4f);
        var samples = converter.Finish();
        Assert.InRange(samples.Length, 15990, 16040);
        Assert.InRange(samples.Max(), (short)15500, (short)17000); // 0.5 full scale
        Assert.Equal(0f, converter.Level);
    }

    [Fact]
    public void Pcm16_mono_16k_is_copied()
    {
        var converter = new CaptureConverter(new SampleFormat(16000, 1, SampleEncoding.Pcm16));
        var wav = WavEncoder.Encode(new short[] { 100, -200, 300 });
        converter.Push(wav.AsSpan(44));
        Assert.Equal(new short[] { 100, -200, 300 }, converter.Finish());
    }

    [Fact]
    public void Pcm24_is_decoded()
    {
        var f = new SampleFormat(16000, 1, SampleEncoding.Pcm24);
        var dest = new float[1];
        f.DecodeToMono(new byte[] { 0x00, 0x00, 0x40 }, dest); // 0x400000 = 0.5 of full scale
        Assert.Equal(0.5f, dest[0], 3);
    }
}

public class ClipAnalyzerTests
{
    private static short[] Noise(double seconds, double amplitude, int seed = 1)
    {
        var rnd = new Random(seed);
        return Enumerable.Range(0, (int)(16000 * seconds))
            .Select(_ => (short)(((rnd.NextDouble() * 2) - 1) * amplitude * 32767)).ToArray();
    }

    [Fact]
    public void Under_0_6_seconds_is_too_short() =>
        Assert.Equal(ClipVerdict.TooShort, ClipAnalyzer.Analyze(Noise(0.5, 0.3)));

    [Fact]
    public void Near_silence_is_silent() =>
        Assert.Equal(ClipVerdict.Silent, ClipAnalyzer.Analyze(Noise(3, 0.002)));

    [Fact]
    public void All_zero_is_silent() =>
        Assert.Equal(ClipVerdict.Silent, ClipAnalyzer.Analyze(new short[16000 * 2]));

    [Fact]
    public void Speech_level_audio_is_ok() =>
        Assert.Equal(ClipVerdict.Ok, ClipAnalyzer.Analyze(Noise(1.0, 0.1)));

    [Fact]
    public void A_single_click_does_not_count_as_speech()
    {
        var samples = new short[16000 * 2];
        for (var i = 1000; i < 1100; i++)
        {
            samples[i] = 20000;
        }

        Assert.Equal(ClipVerdict.Silent, ClipAnalyzer.Analyze(samples));
    }

    [Fact]
    public void Quiet_words_in_silence_are_ok()
    {
        var samples = new short[16000 * 3];
        Noise(0.2, 0.05).CopyTo(samples, 16000); // 200 ms of speech-level sound
        Assert.Equal(ClipVerdict.Ok, ClipAnalyzer.Analyze(samples));
    }
}

public class ToneGeneratorTests
{
    [Fact]
    public void Sounds_are_short_valid_wavs()
    {
        foreach (var wav in new[] { ToneGenerator.StartSound(), ToneGenerator.StopSound() })
        {
            var (samples, rate) = WavEncoder.Decode(wav);
            Assert.Equal(22050, rate);
            Assert.InRange(samples.Length / (double)rate, 0.1, 0.3);
            Assert.True(samples.Max() < short.MaxValue / 4, "should be soft");
        }
    }
}
