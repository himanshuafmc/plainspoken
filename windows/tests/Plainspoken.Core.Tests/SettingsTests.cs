using System.Text.Json;
using System.Text.Json.Nodes;
using Plainspoken.Core.Logging;
using Plainspoken.Core.Settings;

namespace Plainspoken.Core.Tests;

public class SettingsTests
{
    private static PlainspokenSettings Sample() => new PlainspokenSettings
    {
        Transcription =
        {
            Engine = EngineKind.Generate,
            Languages = LanguagePreset.Auto,
            Mode = TranscriptionMode.Verbatim,
            CustomVocabulary = ["WhatsApp", "PowerPoint"],
            RequestTimeoutSeconds = 90,
        },
        Recording = { MaxSeconds = 120, Sounds = false },
        Insertion = { Method = InsertionMethod.Type, TrailingSpace = false },
        History = { Enabled = false },
        Hotkey = "Ctrl+Shift+F9",
        Local = { ApiKeyProtected = "AQAAANCMnd8BFdERjHoAwE/Cl+s=", MicrophoneId = "{mic}", StartWithWindows = true, FirstRunCompleted = true },
    }.Normalize();

    [Fact]
    public void Round_trips_every_field()
    {
        var original = Sample();
        var copy = SettingsSerializer.Deserialize(SettingsSerializer.Serialize(original));
        Assert.Equal(SettingsSerializer.Serialize(original), SettingsSerializer.Serialize(copy));
        Assert.Equal(EngineKind.Generate, copy.Transcription.Engine);
        Assert.Equal("AQAAANCMnd8BFdERjHoAwE/Cl+s=", copy.Local.ApiKeyProtected);
        Assert.Equal(["WhatsApp", "PowerPoint"], copy.Transcription.CustomVocabulary);
    }

    [Fact]
    public void Json_uses_camelCase_names_and_string_enums()
    {
        var json = SettingsSerializer.Serialize(Sample());
        Assert.Contains("\"schemaVersion\": 1", json, StringComparison.Ordinal);
        Assert.Contains("\"engine\": \"generate\"", json, StringComparison.Ordinal);
        Assert.Contains("\"languages\": \"auto\"", json, StringComparison.Ordinal);
        Assert.Contains("\"method\": \"type\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Export_never_contains_the_key_or_local_section()
    {
        var export = SettingsSerializer.Export(Sample());
        Assert.DoesNotContain("local", export, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("apiKey", export, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("AQAAANCMnd8BFdERjHoAwE", export, StringComparison.Ordinal);
        Assert.DoesNotContain("{mic}", export, StringComparison.Ordinal);
        Assert.Contains("WhatsApp", export, StringComparison.Ordinal);
    }

    [Fact]
    public void Import_applies_shared_values_and_keeps_local_ones()
    {
        var mine = new PlainspokenSettings { Local = { ApiKeyProtected = "mine", MicrophoneId = "my-mic", FirstRunCompleted = true } }.Normalize();
        var theirs = Sample();
        theirs.Local.ApiKeyProtected = "theirs";

        var imported = SettingsSerializer.Import(SettingsSerializer.Serialize(theirs), mine);

        Assert.Equal("mine", imported.Local.ApiKeyProtected);
        Assert.Equal("my-mic", imported.Local.MicrophoneId);
        Assert.Equal(EngineKind.Generate, imported.Transcription.Engine);
        Assert.Equal(["WhatsApp", "PowerPoint"], imported.Transcription.CustomVocabulary);
        Assert.Equal("Ctrl+Shift+F9", imported.Hotkey);
    }

    [Fact]
    public void Missing_fields_get_defaults_and_unknown_values_are_tolerated()
    {
        var s = SettingsSerializer.Deserialize("""
            { "transcription": { "engine": "quantum", "mode": "verbatim", "newThing": 5 },
              "recording": { "maxSeconds": 99999 }, "hotkey": "Space", "futureSection": {} }
            """);
        Assert.Equal(EngineKind.Transcribe, s.Transcription.Engine);
        Assert.Equal(TranscriptionMode.Verbatim, s.Transcription.Mode);
        Assert.Equal(LanguagePreset.EnglishHindi, s.Transcription.Languages);
        Assert.Equal(1800, s.Recording.MaxSeconds);
        Assert.Equal(PlainspokenSettings.DefaultHotkey, s.Hotkey);
        Assert.True(s.Insertion.TrailingSpace);
        Assert.Equal(TranscriptionSettings.DefaultTranscribeModel, s.Transcription.TranscribeModel);
        Assert.True(s.Transcription.LiveStreaming); // on by default since v0.1.0
        Assert.True(s.ShowMiniButton);
    }

    [Fact]
    public void Null_sections_and_bad_urls_are_repaired()
    {
        var s = SettingsSerializer.Deserialize("""{ "transcription": null, "local": null }""");
        Assert.NotNull(s.Transcription);
        Assert.NotNull(s.Local);

        var t = SettingsSerializer.Deserialize("""{ "transcription": { "apiBaseUrl": "ftp://x", "requestTimeoutSeconds": 1 } }""");
        Assert.Equal(TranscriptionSettings.DefaultApiBaseUrl, t.Transcription.ApiBaseUrl);
        Assert.Equal(10, t.Transcription.RequestTimeoutSeconds);
        Assert.Equal("https://example.test/proxy", PlainspokenSettings.NormalizeBaseUrl("https://example.test/proxy/"));
    }

    [Fact]
    public void Exported_properties_are_all_in_the_shared_schema()
    {
        var schema = JsonNode.Parse(Fixtures.Shared("settings.schema.json"))!;
        var export = JsonNode.Parse(SettingsSerializer.Export(Sample()))!.AsObject();
        var rootProps = schema["properties"]!.AsObject();
        foreach (var (name, value) in export)
        {
            Assert.True(rootProps.ContainsKey(name), $"'{name}' missing from settings.schema.json");
            if (value is JsonObject section)
            {
                var sectionProps = rootProps[name]!["properties"]!.AsObject();
                foreach (var (child, _) in section)
                {
                    Assert.True(sectionProps.ContainsKey(child), $"'{name}.{child}' missing from settings.schema.json");
                }
            }
        }
    }

    [Fact]
    public void Schema_enums_match_what_we_write()
    {
        var schema = JsonNode.Parse(Fixtures.Shared("settings.schema.json"))!;
        var engines = schema["properties"]!["transcription"]!["properties"]!["engine"]!["enum"]!.AsArray().Select(n => n!.GetValue<string>());
        foreach (var e in Enum.GetValues<EngineKind>())
        {
            var s = new PlainspokenSettings { Transcription = { Engine = e } };
            var written = JsonNode.Parse(SettingsSerializer.Export(s))!["transcription"]!["engine"]!.GetValue<string>();
            Assert.Contains(written, engines);
        }
    }

    [Fact]
    public void Store_saves_atomically_and_quarantines_corrupt_files()
    {
        using var dir = new TempDir();
        var store = new SettingsStore(dir.File("settings.json"), NullLog.Instance);
        Assert.False(store.Exists);
        var fresh = store.Load(() => new PlainspokenSettings { Transcription = { CustomVocabulary = ["seed"] } });
        Assert.Equal(["seed"], fresh.Transcription.CustomVocabulary);

        store.Save(Sample());
        Assert.Equal(EngineKind.Generate, store.Load(() => new PlainspokenSettings()).Transcription.Engine);
        Assert.False(File.Exists(dir.File("settings.json.tmp")));

        File.WriteAllText(dir.File("settings.json"), "{ not json");
        var recovered = store.Load(() => new PlainspokenSettings());
        Assert.Equal(EngineKind.Transcribe, recovered.Transcription.Engine);
        Assert.Single(Directory.GetFiles(dir.Path, "settings.json.bad-*"));
    }
}

public class VocabularyTests
{
    [Fact]
    public void Trims_collapses_spaces_drops_blanks_and_comments()
    {
        var result = VocabularyCleaner.FromText("  Google   Meet \r\n\r\n# a comment\n\tGmail\n   \n");
        Assert.Equal(["Google Meet", "Gmail"], result);
    }

    [Fact]
    public void Deduplicates_case_insensitively_keeping_first_spelling()
    {
        Assert.Equal(["YouTube", "Excel"], VocabularyCleaner.Clean(["YouTube", "youtube", "Excel", "YouTube "]));
    }

    [Fact]
    public void Caps_at_1000_terms_and_100_chars()
    {
        var many = Enumerable.Range(0, 1500).Select(i => $"term{i}").Append(new string('x', 101));
        var result = VocabularyCleaner.Clean(many);
        Assert.Equal(1000, result.Count);
        Assert.Equal("term999", result[^1]);
        Assert.DoesNotContain(result, t => t.Length > 100);
    }

    [Fact]
    public void Seed_file_has_the_expected_terms()
    {
        var seed = VocabularyCleaner.FromText(Fixtures.Shared("vocabulary.default.txt"));
        Assert.Equal(11, seed.Count);
        Assert.Contains("Google Meet", seed);
        Assert.Contains("Plainspoken", seed);
        Assert.Equal("WhatsApp", seed[0]);
    }

    [Fact]
    public void Language_presets_map_to_codes()
    {
        Assert.Equal(["en-IN", "hi-IN"], LanguagePresets.Codes(LanguagePreset.EnglishHindi));
        Assert.Equal(["en-IN"], LanguagePresets.Codes(LanguagePreset.English));
        Assert.Empty(LanguagePresets.Codes(LanguagePreset.Auto));
    }
}

public class HotkeyTests
{
    [Theory]
    [InlineData("Ctrl+Alt+Space", "Ctrl+Alt+Space")]
    [InlineData("alt + control + space", "Ctrl+Alt+Space")]
    [InlineData("Win+Shift+f9", "Shift+Win+F9")]
    [InlineData("Ctrl+D1", "Ctrl+D1")]
    public void Parses_and_normalises(string input, string expected)
    {
        Assert.True(HotkeyGesture.TryParse(input, out var g, out _));
        Assert.Equal(expected, g.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("Space")]
    [InlineData("Shift+A")]
    [InlineData("Ctrl+Alt")]
    [InlineData("Ctrl+A+B")]
    public void Rejects_unsafe_or_incomplete(string input)
    {
        Assert.False(HotkeyGesture.TryParse(input, out _, out var error));
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void Modifier_values_match_win32()
    {
        Assert.Equal(1, (int)HotkeyModifiers.Alt);
        Assert.Equal(2, (int)HotkeyModifiers.Ctrl);
        Assert.Equal(4, (int)HotkeyModifiers.Shift);
        Assert.Equal(8, (int)HotkeyModifiers.Win);
    }
}
