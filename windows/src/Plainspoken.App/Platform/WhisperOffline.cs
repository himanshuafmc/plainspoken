using System.Diagnostics;
using System.Text;
using Plainspoken.Core.Logging;
using Plainspoken.Core.Offline;
using Plainspoken.Core.Settings;
using Plainspoken.Core.Transcription;
using Whisper.net;

namespace Plainspoken.App.Platform;

/// <summary>
/// Offline transcription with whisper.cpp (through Whisper.net) and the model downloaded in Settings → Offline. The model
/// is loaded on first use and kept while it stays the chosen one; one dictation is transcribed at a time.
/// </summary>
internal sealed class WhisperOffline(ModelStore store, Func<PlainspokenSettings> settings, ILog log) : IOfflineTranscriber, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private WhisperFactory? _factory;
    private string? _loaded;

    public static OfflineModel ModelFor(PlainspokenSettings s)
    {
        ArgumentNullException.ThrowIfNull(s);
        return OfflineModels.Find(s.Local.OfflineModel, OfflineModels.Pc);
    }

    public bool IsReady(PlainspokenSettings s) => s?.Local.OfflineEnabled == true && store.IsDownloaded(ModelFor(s));

    public async Task<string> TranscribeAsync(TranscriptionRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var model = ModelFor(settings());
            var path = store.FileOf(model);
            var sw = Stopwatch.StartNew();
            if (_factory is null || _loaded != path)
            {
                _factory?.Dispose();
                _factory = null;
                _factory = await Task.Run(() => WhisperFactory.FromPath(path), ct).ConfigureAwait(false);
                _loaded = path;
            }

            var builder = _factory.CreateBuilder()
                .WithLanguage(OfflineWhisper.Language(request))
                .WithThreads(Math.Clamp(Environment.ProcessorCount - 1, 1, 8));
            var prompt = OfflineWhisper.Prompt(request);
            if (prompt.Length > 0)
            {
                builder = builder.WithPrompt(prompt);
            }

            var text = new StringBuilder();
            await using (var processor = builder.Build())
            {
                using var wav = new MemoryStream(request.Wav, writable: false);
                await foreach (var segment in processor.ProcessAsync(wav, ct).ConfigureAwait(false))
                {
                    text.Append(segment.Text).Append(' ');
                }
            }

            log.Info($"offline: {model.Id} took {sw.ElapsedMilliseconds} ms for {request.DurationSeconds:0.0}s");
            return OfflineWhisper.Tidy(text.ToString());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.Warn($"offline: {ex.GetType().Name} {Redactor.Clean(ex.Message, 160)}");
            throw new TranscriptionException(TranscriptionErrorKind.UnexpectedResponse, "offline transcription failed", inner: ex);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _factory?.Dispose();
        _gate.Dispose();
    }
}
