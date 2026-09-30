using Plainspoken.Core.Dictation;
using Plainspoken.Core.Logging;
using Plainspoken.Core.Transcription;

namespace Plainspoken.Core.Tests;

public class TranscriptJoinerTests
{
    private static string Join(params string[] pieces) => TranscriptJoiner.Join(pieces);

    [Fact]
    public void Trimmed_phrases_get_spaces_at_word_and_sentence_boundaries()
    {
        // The reported bug: pieces cut at pauses and sentence ends, each without its spaces.
        Assert.Equal("Please add milk, eggs and bread. The total is 2,500 rupees.",
            Join("Please add milk,", "eggs and", "bread.", "The total is", "2,500 rupees."));
        Assert.Equal("I will call you tomorrow morning. Please wait.",
            Join("I will call you tomorrow", "morning.", "Please wait."));
    }

    [Fact]
    public void Pieces_that_carry_their_own_spaces_are_kept_exactly()
    {
        Assert.Equal("The transcription is ready.", Join("The", " trans", "cription", " is", " ready", "."));
        Assert.Equal("Hello world, how are you?", Join("Hello ", "world, ", "how are ", "you?"));
    }

    [Fact]
    public void A_missing_space_after_a_sentence_is_added_even_in_token_streams()
    {
        Assert.Equal("It is done. Next one.", Join("It", " is", " done.", "Next", " one."));
    }

    [Fact]
    public void Single_word_tokens_without_spaces_are_not_split()
    {
        Assert.Equal("Okay.", Join("Ok", "ay", "."));
        Assert.Equal("WhatsApp", Join("Whats", "App"));
    }

    [Theory]
    [InlineData("The price is 2.", "5 lakh rupees", "The price is 2.5 lakh rupees")]
    [InlineData("Meet at 10:", "30 AM", "Meet at 10:30 AM")]
    [InlineData("It costs 1,", "000 rupees", "It costs 1,000 rupees")]
    [InlineData("See you at 10 a.", "m. sharp", "See you at 10 a.m. sharp")]
    [InlineData("Open www.", "example.com now", "Open www.example.com now")]
    [InlineData("Mail me at name@example.", "com today", "Mail me at name@example.com today")]
    [InlineData("Open https:", "//example.com", "Open https://example.com")]
    [InlineData("It is well-", "known now", "It is well-known now")]
    [InlineData("He said \"", "hello\" to me", "He said \"hello\" to me")]
    [InlineData("He said", "\"hello\" to me", "He said \"hello\" to me")]
    [InlineData("Is it ready", "? Yes it is", "Is it ready? Yes it is")]
    [InlineData("Call Ravi (", "my friend) now", "Call Ravi (my friend) now")]
    [InlineData("It is done", "'s fine now", "It is done's fine now")]
    [InlineData("Kal meeting hai.", "Please aana", "Kal meeting hai. Please aana")]
    [InlineData("मुझे कल जाना है।", "फिर आऊँगा", "मुझे कल जाना है। फिर आऊँगा")]
    [InlineData("मुझे कल जाना", "है अभी", "मुझे कल जाना है अभी")]
    public void Boundaries(string left, string right, string expected) => Assert.Equal(expected, Join(left, right));

    [Fact]
    public void Combining_marks_always_attach()
    {
        // A piece that starts with a Devanagari vowel sign continues the previous syllable.
        Assert.Equal("कल सुबह जाना है", Join("कल सुबह ज", "ाना है"));
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
    [Theory]
    [InlineData("See you tomorrow.Please call me.", "See you tomorrow. Please call me.")]
    [InlineData("Really?Yes!Great.", "Really? Yes! Great.")]
    [InlineData("Milk,eggs and bread", "Milk, eggs and bread")]
    [InlineData("Note:Please bring the file.", "Note: Please bring the file.")]
    [InlineData("मुझे कल जाना है।फिर आऊँगा", "मुझे कल जाना है। फिर आऊँगा")]
    [InlineData("Kal jana hai.मैं आऊँगा", "Kal jana hai. मैं आऊँगा")]
    [InlineData("It costs ₹2,500 or 2.5 lakh at 10:30 AM.", "It costs ₹2,500 or 2.5 lakh at 10:30 AM.")]
    [InlineData("Made in the U.S.A. at 10 a.m. e.g. today", "Made in the U.S.A. at 10 a.m. e.g. today")]
    [InlineData("Open www.Example.com or https://Example.com", "Open www.Example.com or https://Example.com")]
    [InlineData("Write to name@Example.com", "Write to name@Example.com")]
    [InlineData("Use Node.js and ASP.NET", "Use Node.js and ASP.NET")]
    [InlineData("1.First item", "1.First item")]
    [InlineData("Already fine. Nothing to do.", "Already fine. Nothing to do.")]
    public void Adds_only_the_missing_space(string input, string expected) =>
        Assert.Equal(expected, TextPostProcessor.FixSpaceAfterPunctuation(input));

    [Fact]
    public void Clean_applies_the_fix() =>
        Assert.Equal("Done. Next.", TextPostProcessor.Clean("  Done.Next.  "));
}
