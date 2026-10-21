using NAudio.MediaFoundation;
using NAudio.Wave;
using Plainspoken.Core.Audio;

namespace Plainspoken.App.Platform;

/// <summary>
/// Compresses a recording to AAC in an MP4 container with Windows' own Media Foundation encoder, for services that accept
/// compressed audio (about a third of the WAV size). The encoder takes 44.1 or 48 kHz only, so the 16 kHz recording is
/// resampled first. Returns null (the WAV is sent instead) if this Windows has no AAC encoder.
/// </summary>
internal sealed class AacCompressor : IAudioCompressor
{
    private const int EncodeRate = 48_000;
    private const int BitRate = 96_000; // the lowest rate the Windows AAC encoder offers

    private static readonly Lazy<bool> Started = new(() =>
    {
        MediaFoundationApi.Startup();
        return true;
    });

    public EncodedAudio? Compress(short[] samples, int sampleRate)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (!Started.Value || samples.Length == 0)
        {
            return null;
        }

        var resampler = new StreamingResampler(sampleRate, EncodeRate);
        var floats = new float[samples.Length];
        for (var i = 0; i < samples.Length; i++)
        {
            floats[i] = samples[i] / 32768f;
        }

        var output = new List<float>(samples.Length * EncodeRate / sampleRate + 64);
        resampler.Process(floats, output);
        resampler.Flush(output);
        var pcm = new byte[output.Count * 2];
        for (var i = 0; i < output.Count; i++)
        {
            var s = (short)Math.Round(Math.Clamp(output[i], -1f, 1f) * 32767f);
            pcm[i * 2] = (byte)s;
            pcm[(i * 2) + 1] = (byte)(s >> 8);
        }

        using var source = new RawSourceWaveStream(new MemoryStream(pcm), new WaveFormat(EncodeRate, 16, 1));
        using var encoded = new MemoryStream();
        MediaFoundationEncoder.EncodeToAac(source, encoded, BitRate);
        return encoded.Length == 0 ? null : new EncodedAudio(encoded.ToArray(), "audio/mp4", "audio.m4a");
    }
}
