// Live smoke test for the speech services, using the real Core code paths.
//
//   dotnet run --project windows/tools/Plainspoken.Smoke -- [--service gemini|gemini-general|sarvam|openai|deepgram|elevenlabs|both]
//        [--cleanup] [--mode smart|verbatim] [--lang englishHindi|auto|english] file1.wav [file2.wav ...]
//
// "both" means the two Gemini services (the default). --cleanup runs the whole router, including the Gemini clean-up
// step (also for Gemini's own text, so it can be tried with a Gemini key alone). Keys come from environment variables only: GEMINI_API_KEY, SARVAM_API_KEY, OPENAI_API_KEY, DEEPGRAM_API_KEY,
// ELEVENLABS_API_KEY. Without GEMINI_API_KEY no Gemini key header is sent (for environments where a proxy injects the
// key). Input WAVs may be any sample rate, mono or stereo PCM16; they are converted to 16 kHz mono like the app does.
// make-clips.sh (next to this file) generates synthetic test clips with espeak-ng.
using Plainspoken.Core.Audio;
using Plainspoken.Core.Logging;
using Plainspoken.Core.Settings;
using Plainspoken.Core.Transcription;

var files = new List<string>();
var services = new List<SpeechService> { SpeechService.Gemini, SpeechService.GeminiGeneral };
var cleanup = false;
var settings = new PlainspokenSettings().Normalize();
settings.Transcription.CustomVocabulary = VocabularyCleaner.FromText(ReadSeedVocabulary());

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--service":
            var id = args[++i];
            services = id == "both"
                ? [SpeechService.Gemini, SpeechService.GeminiGeneral]
                : [SpeechServices.ById(id) ?? throw new ArgumentException("Unknown service: " + id)];
            break;
        case "--cleanup":
            cleanup = true;
            settings.Transcription.Cleanup.AlsoGemini = true; // so the clean-up step can be tried with a Gemini key alone
            break;
        case "--mode":
            settings.Transcription.Mode = Enum.Parse<TranscriptionMode>(args[++i], true);
            break;
        case "--lang":
            settings.Transcription.Languages = Enum.Parse<LanguagePreset>(args[++i], true);
            break;
        default:
            files.Add(args[i]);
            break;
    }
}

if (files.Count == 0)
{
    Console.Error.WriteLine("usage: Plainspoken.Smoke [--service id|both] [--cleanup] [--mode smart|verbatim] [--lang ...] file.wav ...");
    return 2;
}

var geminiKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY");
Console.WriteLine(string.IsNullOrEmpty(geminiKey) ? "No GEMINI_API_KEY: sending no Gemini key header (proxy mode)." : "Using GEMINI_API_KEY.");
string? KeyFor(SpeechService s) => s.IsGemini() ? geminiKey : Environment.GetEnvironmentVariable(s.KeySlot().ToUpperInvariant() + "_API_KEY");
using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
var log = new ConsoleLog();
var factory = TranscriptionService.Engines(http, KeyFor, log, requireGeminiKey: !string.IsNullOrEmpty(geminiKey));
var router = new TranscriptionService(factory, s => s.IsGemini() || !string.IsNullOrEmpty(KeyFor(s)), log,
    CleanerFactory.Standard(http, () => geminiKey, () => null, log, requireGeminiKey: !string.IsNullOrEmpty(geminiKey)));
var failures = 0;

foreach (var file in files)
{
    var wav = To16kMono(File.ReadAllBytes(file), out var seconds);
    foreach (var service in services)
    {
        Console.WriteLine($"--- {Path.GetFileName(file)} ({seconds:0.0}s) via {service.Id()}{(cleanup ? " + clean-up" : "")}");
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var request = TranscriptionRequest.From(wav, seconds, settings);
            string text;
            if (cleanup)
            {
                settings.Transcription.ServiceOrder = [service.Id()];
                var outcome = await router.TranscribeAsync(request, settings, null, CancellationToken.None);
                text = outcome.Text + (outcome.CleanedBy is null ? "" : $"  [cleaned by {outcome.CleanedBy}]");
            }
            else
            {
                text = await factory(service, settings, 2).TranscribeAsync(request, CancellationToken.None);
            }

            Console.WriteLine($"OK in {sw.ElapsedMilliseconds} ms: {text}");
            await Task.Delay(1500); // let the background file delete finish before the next call
        }
        catch (TranscriptionException ex)
        {
            failures++;
            Console.WriteLine($"FAILED {ex.Kind}: {ex.Message}");
        }
    }
}

return failures == 0 ? 0 : 1;

static byte[] To16kMono(byte[] input, out double seconds)
{
    var (samples, rate) = WavEncoder.Decode(input);
    if (rate != WavEncoder.TargetSampleRate)
    {
        var resampler = new StreamingResampler(rate);
        var output = new List<float>();
        resampler.Process(samples.Select(s => s / 32768f).ToArray(), output);
        resampler.Flush(output);
        samples = output.Select(v => (short)Math.Round(Math.Clamp(v, -1f, 1f) * 32767f)).ToArray();
    }

    seconds = WavEncoder.DurationSeconds(samples.Length);
    return WavEncoder.Encode(samples);
}

static string ReadSeedVocabulary()
{
    for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
    {
        var path = Path.Combine(dir.FullName, "shared", "vocabulary.default.txt");
        if (File.Exists(path))
        {
            return File.ReadAllText(path);
        }
    }

    return string.Empty;
}

internal sealed class ConsoleLog : ILog
{
    public void Info(string message) => Console.WriteLine("  [info] " + Redactor.Clean(message));

    public void Warn(string message) => Console.WriteLine("  [warn] " + Redactor.Clean(message));

    public void Error(string message, Exception? exception = null) =>
        Console.WriteLine("  [error] " + Redactor.Clean(message) + (exception is null ? "" : " " + exception.GetType().Name));
}
