using Plainspoken.Core.Dictation;
using Plainspoken.Core.Logging;
using Plainspoken.Core.Transcription;

namespace Plainspoken.Core.Tests;

public class TranscriptJoinerTests
{
    private static string Join(params string[] pieces) => TranscriptJoiner.Join(pieces);

    public static TheoryData<string, string[], string> JoinCases()
    {
        var data = new TheoryData<string, string[], string>();
        using var doc = System.Text.Json.JsonDocument.Parse(Fixtures.Read("text-cases.json"));
        foreach (var c in doc.RootElement.GetProperty("join").EnumerateArray())
        {
            data.Add(c.GetProperty("note").GetString()!,
                c.GetProperty("pieces").EnumerateArray().Select(p => p.GetString()!).ToArray(),
                c.GetProperty("expected").GetString()!);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(JoinCases))]
    public void Shared_join_cases(string note, string[] pieces, string expected)
    {
        _ = note;
        Assert.Equal(expected, Join(pieces));
    }

    [Fact]
    public void Empty_pieces_are_ignored_and_stats_count_the_rest()
    {
        var text = TranscriptJoiner.Join(["See you", "", "tomorrow.", "Bye."], out var stats);
        Assert.Equal("See you tomorrow. Bye.", text);
        Assert.Equal(3, stats.Pieces);
        Assert.Equal(0, stats.CarrySpaces);
        Assert.Equal(1, stats.MultiWord);
        Assert.Equal(2, stats.SpacesAdded);
        Assert.DoesNotContain("tomorrow", stats.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Live_transcript_joins_trimmed_input_transcription_chunks()
    {
        var log = new ListLog();
        var transcript = new LiveTranscript(log);
        transcript.Apply("""{"serverContent":{"inputTranscription":{"text":"I will call you tomorrow"}}}""");
        transcript.Apply("""{"serverContent":{"inputTranscription":{"text":"morning."}}}""");
        transcript.Apply("""{"serverContent":{"inputTranscription":{"text":"Please wait."}}}""");

        Assert.Equal("I will call you tomorrow morning. Please wait.", transcript.Finish());
        Assert.Contains(log.Lines, l => l.Contains("3 pieces", StringComparison.Ordinal));
        Assert.DoesNotContain(log.Lines, l => l.Contains("tomorrow", StringComparison.Ordinal));
    }

    [Fact]
    public void Several_text_items_in_one_response_are_joined_with_care()
    {
        using var doc = System.Text.Json.JsonDocument.Parse(
            """{"status":"completed","steps":[{"type":"model_output","content":[{"type":"text","text":"See you tomorrow."},{"type":"text","text":"Please call me."}]}]}""");
        Assert.Equal("See you tomorrow. Please call me.", ResponseParsers.ParseInteraction(doc.RootElement).Text);
    }
}

public class PunctuationSpacingTests
{
    public static TheoryData<string, string> SpacingCases()
    {
        var data = new TheoryData<string, string>();
        using var doc = System.Text.Json.JsonDocument.Parse(Fixtures.Read("text-cases.json"));
        foreach (var c in doc.RootElement.GetProperty("spacing").EnumerateArray())
        {
            data.Add(c.GetProperty("input").GetString()!, c.GetProperty("expected").GetString()!);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(SpacingCases))]
    public void Adds_only_the_missing_space(string input, string expected) =>
        Assert.Equal(expected, TextPostProcessor.FixSpaceAfterPunctuation(input));

    [Fact]
    public void Clean_applies_the_fix() =>
        Assert.Equal("Done. Next.", TextPostProcessor.Clean("  Done.Next.  "));
}
