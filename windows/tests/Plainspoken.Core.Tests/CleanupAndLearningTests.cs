using System.Net;
using System.Text.Json.Nodes;
using Plainspoken.Core.Dictation;
using Plainspoken.Core.Settings;
using Plainspoken.Core.Storage;
using Plainspoken.Core.Transcription;

namespace Plainspoken.Core.Tests;

public class CleanupTests
{
    private static readonly CleanupRequest Request = new(["WhatsApp", "Sameer"], HindiScript.Roman, ["insert my address"]);

    private static GeminiClient Gemini(FakeHttpHandler http) =>
        new(new HttpClient(http), TranscriptionSettings.DefaultApiBaseUrl, () => "k", new ListLog(), new RetryPolicy(delay: NoDelay.Delay));

    [Fact]
    public void The_prompt_covers_fillers_grammar_corrections_and_never_translating_or_answering()
    {
        var p = CleanupPrompt.System(HindiScript.Roman);
        foreach (var phrase in new[] { "um", "matlab", "grammar", "corrects themselves", "Translate", "Answer questions", "Roman letters" })
        {
            Assert.Contains(phrase, p, StringComparison.Ordinal);
        }

        Assert.Contains("Devanagari (for example", CleanupPrompt.System(HindiScript.Devanagari), StringComparison.Ordinal);
        var user = CleanupPrompt.User("umm hello", Request);
        Assert.StartsWith("Spelling list: WhatsApp; Sameer", user, StringComparison.Ordinal);
        Assert.Contains("insert my address", user, StringComparison.Ordinal);
        Assert.EndsWith("<dictation>\numm hello\n</dictation>", user, StringComparison.Ordinal);
    }

    [Fact]
    public void The_prompt_is_the_same_text_as_on_Android()
    {
        var kotlin = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "android-cleanup.kt.txt"));
        var start = kotlin.IndexOf("You clean up dictated text.", StringComparison.Ordinal);
        var end = kotlin.IndexOf("If the text is empty or only filler, return nothing.", StringComparison.Ordinal);
        var android = string.Join("\n", kotlin[start..end].Split('\n').Select(l => l.Trim()));
        var windows = string.Join("\n", CleanupPrompt.System(HindiScript.Roman).Split('\n').Select(l => l.Trim()));
        var scriptLine = android.IndexOf("Script: ", StringComparison.Ordinal);
        Assert.StartsWith(android[..scriptLine], windows, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Gemini_cleanup_asks_for_minimal_thinking_on_Gemini_3_models()
    {
        var http = new FakeHttpHandler((_, _) => FakeHttpHandler.Json(HttpStatusCode.OK, """{"candidates":[{"content":{"parts":[{"text":"Hello."}]}}]}"""));
        var cleaner = new GeminiCleaner(Gemini(http), "gemini-3.5-flash", TimeSpan.FromSeconds(10));
        Assert.Equal("Hello.", await cleaner.CleanAsync("umm hello", Request, default));
        var r = Assert.Single(http.Requests);
        Assert.EndsWith("/v1beta/models/gemini-3.5-flash:generateContent", r.Uri.AbsoluteUri, StringComparison.Ordinal);
        Assert.Contains("\"thinkingLevel\":\"minimal\"", r.Body!, StringComparison.Ordinal);
        Assert.Contains("\"temperature\":0", r.Body!, StringComparison.Ordinal);
        Assert.Contains("Never:", r.Body!, StringComparison.Ordinal);
        Assert.Equal("gemini:gemini-3.5-flash", cleaner.Name);
    }

    [Fact]
    public async Task A_model_that_rejects_the_thinking_setting_is_asked_again_without_it()
    {
        var http = new FakeHttpHandler((r, _) => r.Body!.Contains("thinking", StringComparison.Ordinal)
            ? FakeHttpHandler.Json(HttpStatusCode.BadRequest, """{"error":{"code":400,"message":"Unknown name thinkingLevel","status":"INVALID_ARGUMENT"}}""")
            : FakeHttpHandler.Json(HttpStatusCode.OK, """{"candidates":[{"content":{"parts":[{"text":"Fine."}]}}]}"""));
        var cleaner = new GeminiCleaner(Gemini(http), "gemini-3-flash-preview", TimeSpan.FromSeconds(10));
        Assert.Equal("Fine.", await cleaner.CleanAsync("uh fine", Request, default));
        Assert.Equal(2, http.Requests.Count);
        Assert.Null(GeminiCleaner.ThinkingConfig("gemma-3"));
        Assert.Equal("""{"thinkingBudget":0}""", GeminiCleaner.ThinkingConfig("gemini-2.5-flash")!.ToJsonString());
    }

    [Fact]
    public async Task OpenAI_compatible_cleanup_uses_chat_completions()
    {
        var http = new FakeHttpHandler((_, _) => FakeHttpHandler.Json(HttpStatusCode.OK, """{"choices":[{"message":{"role":"assistant","content":"Kal meeting hai."}}]}"""));
        var cleaner = new OpenAICleaner(new HttpClient(http), "https://api.groq.com/openai/v1", "llama-3.3-70b-versatile", () => "k", new ListLog(), TimeSpan.FromSeconds(10));
        Assert.Equal("Kal meeting hai.", await cleaner.CleanAsync("umm kal meeting hai", Request, default));
        var r = Assert.Single(http.Requests);
        Assert.Equal("https://api.groq.com/openai/v1/chat/completions", r.Uri.AbsoluteUri);
        Assert.Equal("Bearer k", r.Headers["Authorization"]);
        Assert.Contains("\"role\":\"system\"", r.Body!, StringComparison.Ordinal);
        Assert.Contains("llama-3.3-70b-versatile", r.Body!, StringComparison.Ordinal);
    }

    [Fact]
    public void The_factory_follows_the_provider_setting()
    {
        var factory = CleanerFactory.Standard(new HttpClient(), () => "g", () => "o", new ListLog());
        var s = new PlainspokenSettings().Normalize();
        Assert.IsType<GeminiCleaner>(factory(s));
        s.Transcription.Cleanup.Provider = CleanupProvider.OpenAI;
        Assert.Equal("openai:gpt-4o-mini", factory(s)!.Name);
        s.Transcription.Cleanup.Provider = CleanupProvider.Off;
        Assert.Null(factory(s));
    }

    [Fact]
    public void The_guard_refuses_summaries_answers_and_empty_replies()
    {
        const string raw = "okay so the client called and they want the report by Thursday and the sales numbers from finance";
        Assert.Equal("The client called; they want the report by Thursday and the sales numbers from finance.",
            CleanupGuard.Accept(raw, "<dictation>The client called; they want the report by Thursday and the sales numbers from finance.</dictation>"));
        Assert.Null(CleanupGuard.Accept(raw, "Report due Thursday."));
        Assert.Null(CleanupGuard.Accept("what is the capital of France",
            "The capital of France is Paris, a city known for the Eiffel Tower, museums and its long history as a centre of art."));
        Assert.Null(CleanupGuard.Accept(raw, "  "));
        Assert.Equal("", CleanupGuard.Accept("umm uh hmm", ""));
        Assert.Null(CleanupGuard.Accept(raw, null));
    }

    [Fact]
    public void The_local_filter_removes_only_unambiguous_filler_sounds()
    {
        Assert.Equal("So I wanted to check, are you free?", LocalFillerRemover.Clean("Um, so I wanted to uh check, umm are you free?"));
        Assert.Equal("Aa jao, kal milte hain.", LocalFillerRemover.Clean("Aa jao, aaa kal milte hain."));
        Assert.Equal("Hmm-free text stays: summer, umbrella, human.", LocalFillerRemover.Clean("Hmm-free text stays: summer, umbrella, human."));
        Assert.Equal("please send it", LocalFillerRemover.Clean("uh please send it"));
        Assert.Equal("Please send it", LocalFillerRemover.Clean("Uh, please send it"));
        Assert.True(LocalFillerRemover.IsOnlyFiller("umm, uh... hmm"));
        Assert.False(LocalFillerRemover.IsOnlyFiller("umm yes"));
    }

    [Fact]
    public void The_shared_cleanup_cases_file_is_valid()
    {
        var cases = JsonNode.Parse(Fixtures.Read("cleanup-cases.json"))!["cases"]!.AsArray();
        Assert.True(cases.Count >= 25);
        var ids = cases.Select(c => c!["id"]!.ToJsonString()).ToList();
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
        Assert.All(cases, c => Assert.NotNull(c!["raw"]));
    }
}

public class CorrectionLearnerTests
{
    private static readonly string[] Vocab = ["WhatsApp"];

    [Fact]
    public void A_name_the_user_typed_over_a_misheard_word_is_offered()
    {
        const string inserted = "Please send the report to Samir by Friday.";
        const string now = "Earlier text. Please send the report to Sameer by Friday.";
        Assert.Equal(["Sameer"], CorrectionLearner.Candidates(inserted, now, Vocab));
    }

    [Fact]
    public void Capital_changes_and_two_word_names_are_offered()
    {
        Assert.Equal(["GitHub"], CorrectionLearner.Candidates("I pushed it to github today", "I pushed it to GitHub today", Vocab));
        Assert.Equal(["New Delhi"], CorrectionLearner.Candidates("The office is in new deli now", "The office is in New Delhi now", Vocab));
    }

    [Fact]
    public void Nothing_is_offered_for_unchanged_rewritten_or_already_known_text()
    {
        Assert.Empty(CorrectionLearner.Candidates("Hello there friend", "Hello there friend", Vocab));
        Assert.Empty(CorrectionLearner.Candidates("one two three four five six", "completely different words in this box now", Vocab));
        Assert.Empty(CorrectionLearner.Candidates("send it on whatsapp please", "send it on WhatsApp please", Vocab));
        Assert.Empty(CorrectionLearner.Candidates("we will go there", "We will go there", Vocab)); // sentence start
        Assert.Empty(CorrectionLearner.Candidates("it is a big deal", "it is the big deal", Vocab)); // common word
    }

    [Fact]
    public void Corrections_at_the_start_or_end_of_the_dictation_are_found()
    {
        Assert.Equal(["Kavita"], CorrectionLearner.Candidates("kavitha will call you back", "Kavita will call you back", Vocab));
        Assert.Equal(["Bengaluru"], CorrectionLearner.Candidates("We are flying to bangalore", "We are flying to Bengaluru", Vocab));
    }

    [Fact]
    public void A_long_text_box_is_searched_around_the_dictation()
    {
        var filler = string.Concat(Enumerable.Repeat("Lorem ipsum dolor sit amet, consectetur adipiscing elit. ", 300));
        const string inserted = "Please send the quarterly report to Samir by Friday.";
        var box = filler + "Please send the quarterly report to Sameer by Friday. " + filler;
        var around = CorrectionLearner.Around(inserted, box)!;
        Assert.True(around.Length < 2000);
        Assert.Equal(["Sameer"], CorrectionLearner.Candidates(inserted, around, Vocab));
        Assert.Equal("short box", CorrectionLearner.Around(inserted, "short box"));
        Assert.Null(CorrectionLearner.Around("zzzz yyyy", filler));
    }

    [Fact]
    public void The_watch_hands_back_the_text_only_in_the_same_app_and_only_once()
    {
        var clock = new FakeTime(new DateTimeOffset(2026, 10, 20, 9, 0, 0, TimeSpan.Zero));
        var watch = new CorrectionWatch(clock);
        watch.Inserted("Hello Samir", "notepad");
        Assert.Null(watch.Take("chrome"));
        watch.Inserted("Hello Samir", "notepad");
        Assert.Equal("Hello Samir", watch.Take("notepad"));
        Assert.Null(watch.Take("notepad"));
        watch.Inserted("Hello Samir", "notepad");
        clock.Now += TimeSpan.FromMinutes(11);
        Assert.Null(watch.Take("notepad"));
    }
}

public class HoldToTalkTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 20, 9, 0, 0, TimeSpan.Zero);

    private static DateTimeOffset At(int ms) => T0.AddMilliseconds(ms);

    [Fact]
    public void Holding_the_key_alone_records_until_it_is_let_go()
    {
        var hold = new HoldToTalk();
        hold.KeyDown(At(0));
        Assert.Equal(HoldAction.None, hold.Poll(At(100)));
        hold.KeyDown(At(150)); // key repeat
        Assert.Equal(HoldAction.Start, hold.Poll(At(320)));
        Assert.True(hold.IsHolding);
        Assert.Equal(HoldAction.None, hold.Poll(At(400)));
        Assert.Equal(HoldAction.Stop, hold.KeyUp(At(3000)));
        Assert.False(hold.IsHolding);
    }

    [Fact]
    public void Taps_and_shortcuts_do_nothing()
    {
        var hold = new HoldToTalk();
        hold.KeyDown(At(0));
        Assert.Equal(HoldAction.None, hold.KeyUp(At(120))); // a tap

        hold.KeyDown(At(1000));
        hold.OtherKeyDown(); // Ctrl+C
        Assert.Equal(HoldAction.None, hold.Poll(At(1500)));
        Assert.Equal(HoldAction.None, hold.KeyUp(At(1600)));

        hold.KeyDown(At(2000));
        Assert.Equal(HoldAction.Start, hold.Poll(At(2400)));
        hold.OtherKeyDown(); // typing while recording keeps recording
        Assert.Equal(HoldAction.Stop, hold.KeyUp(At(5000)));
    }
}

public class ServiceMessageTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Messages_name_the_service_that_failed()
    {
        string Text(TranscriptionErrorKind kind, SpeechService? s) =>
            UserMessages.For(new TranscriptionException(kind, "x", service: s), Now, TimeZoneInfo.Utc).Text;

        Assert.Contains("Google took too long", Text(TranscriptionErrorKind.Timeout, SpeechService.Gemini), StringComparison.Ordinal);
        Assert.Contains("Sarvam took too long", Text(TranscriptionErrorKind.Timeout, SpeechService.Sarvam), StringComparison.Ordinal);
        Assert.Contains("Your Deepgram account is out of credits", Text(TranscriptionErrorKind.NoCredits, SpeechService.Deepgram), StringComparison.Ordinal);
        Assert.Contains("Your OpenAI-compatible service key was not accepted", Text(TranscriptionErrorKind.InvalidKey, SpeechService.OpenAI), StringComparison.Ordinal);
        Assert.Contains("Gemini API key", Text(TranscriptionErrorKind.InvalidKey, null), StringComparison.Ordinal);
        Assert.Contains("Free daily limit", Text(TranscriptionErrorKind.DailyQuota, SpeechService.GeminiGeneral), StringComparison.Ordinal);
        Assert.Contains("ElevenLabs's daily limit", Text(TranscriptionErrorKind.DailyQuota, SpeechService.ElevenLabs), StringComparison.Ordinal);
        Assert.Contains("speech service", UserMessages.NoKey.Text, StringComparison.Ordinal);
    }
}

public class StatsTests
{
    [Fact]
    public void Stats_keep_timings_per_service_and_summarise_them()
    {
        using var dir = new TempDir();
        var path = dir.File("stats.json");
        var store = new StatsStore(path, new ListLog(), capacity: 5);
        var t = new DateTimeOffset(2026, 10, 18, 10, 0, 0, TimeSpan.Zero);
        store.Record(new DictationStat(t, SpeechService.Sarvam, 30, 800, 900, Ok: true, Streamed: true, CleanedBy: "gemini:x"));
        store.Record(new DictationStat(t, SpeechService.Sarvam, 30, 1200, 1100, Ok: true));
        store.Record(new DictationStat(t, SpeechService.Sarvam, 10, 5000, 0, Ok: false, ErrorKind: "Timeout"));
        store.Record(new DictationStat(new DateTimeOffset(2026, 9, 1, 10, 0, 0, TimeSpan.Zero), SpeechService.Gemini, 60, 3000, 0, Ok: true));

        var summary = new StatsStore(path, new ListLog()).Summary(t, Time.QuotaReset.FindZone("Asia/Kolkata", "India Standard Time"));
        var sarvam = summary[0];
        Assert.Equal(SpeechService.Sarvam, sarvam.Service);
        Assert.Equal(3, sarvam.Dictations);
        Assert.Equal(1, sarvam.Failures);
        Assert.Equal(1200, sarvam.MedianListenMs);
        Assert.Equal(2300, sarvam.MedianTotalMs);
        Assert.Equal(0.5, sarvam.StreamedShare);
        Assert.Equal(70.0 / 60, sarvam.MinutesThisMonth, 9);
        Assert.Equal(0.0, summary.Single(s => s.Service == SpeechService.Gemini).MinutesThisMonth);

        for (var i = 0; i < 5; i++)
        {
            store.Record(new DictationStat(t, SpeechService.Deepgram, 1, 100, 0, Ok: true));
        }

        Assert.Equal(5, store.All.Count); // capacity
        Assert.DoesNotContain("text\"", File.ReadAllText(path), StringComparison.Ordinal);
        Assert.Contains("\"listenMs\"", File.ReadAllText(path), StringComparison.Ordinal); // same names as Android
    }
}
