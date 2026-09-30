// Live smoke test for the Gemini engines, using the real Core code paths.
//
//   dotnet run --project windows/tools/Plainspoken.Smoke -- [--engine transcribe|generate|both]
//        [--mode smart|verbatim] [--lang englishHindi|auto|english] file1.wav [file2.wav ...]
//
// The key is read from the GEMINI_API_KEY environment variable. If it is not set, no key header
// is sent (for environments where a proxy injects the key). Input WAVs may be any sample rate,
// mono or stereo PCM16; they are converted to 16 kHz mono like the app does.
// make-clips.sh (next to this file) generates synthetic test clips with espeak-ng.
using Plainspoken.Core.Audio;
using Plainspoken.Core.Logging;
using Plainspoken.Core.Settings;
using Plainspoken.Core.Transcription;

var files = new List<string>();
var engines = new List<EngineKind> { EngineKind.Transcribe, EngineKind.Generate };
var settings = new PlainspokenSettings().Normalize();
settings.Transcription.UseBackupEngineWhenLimited = false;
settings.Transcription.CustomVocabulary = VocabularyCleaner.FromText(ReadSeedVocabulary());

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--engine":
            var e = args[++i];
            engines = e == "both" ? [EngineKind.Transcribe, EngineKind.Generate] : [Enum.Parse<EngineKind>(e, true)];
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
    Console.Error.WriteLine("usage: Plainspoken.Smoke [--engine transcribe|generate|both] [--mode smart|verbatim] [--lang ...] file.wav ...");
    return 2;
}

var key = Environment.GetEnvironmentVariable("GEMINI_API_KEY");
Console.WriteLine(string.IsNullOrEmpty(key) ? "No GEMINI_API_KEY: sending no key header (proxy mode)." : "Using GEMINI_API_KEY.");
using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
var log = new ConsoleLog();
var factory = TranscriptionService.GeminiEngines(http, () => key, log, requireApiKey: !string.IsNullOrEmpty(key));
var failures = 0;

foreach (var file in files)
{
    var wav = To16kMono(File.ReadAllBytes(file), out var seconds);
    foreach (var kind in engines)
    {
        Console.WriteLine($"--- {Path.GetFileName(file)} ({seconds:0.0}s) via {kind}");
        try
        {
            var engine = factory(kind, settings);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var text = await engine.TranscribeAsync(TranscriptionRequest.From(wav, seconds, settings), CancellationToken.None);
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
