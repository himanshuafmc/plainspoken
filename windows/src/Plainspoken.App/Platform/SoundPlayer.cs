using Plainspoken.Core.Audio;
using Plainspoken.Core.Dictation;

namespace Plainspoken.App.Platform;

/// <summary>Start/stop chirps generated in code; played asynchronously by Windows.</summary>
internal sealed class Sounds : ISoundPlayer, IDisposable
{
    private readonly System.Media.SoundPlayer _start = Load(ToneGenerator.StartSound());
    private readonly System.Media.SoundPlayer _stop = Load(ToneGenerator.StopSound());

    public void PlayStart() => Play(_start);

    public void PlayStop() => Play(_stop);

    public void Dispose()
    {
        _start.Dispose();
        _stop.Dispose();
    }

    private static System.Media.SoundPlayer Load(byte[] wav)
    {
        var player = new System.Media.SoundPlayer(new MemoryStream(wav));
        player.Load();
        return player;
    }

    private static void Play(System.Media.SoundPlayer player)
    {
        try
        {
            player.Play();
        }
        catch (InvalidOperationException)
        {
            // No audio output device: sounds are optional.
        }
    }
}
