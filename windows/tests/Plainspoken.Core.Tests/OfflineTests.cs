using System.Net;
using System.Security.Cryptography;
using System.Text;
using Plainspoken.Core.Audio;
using Plainspoken.Core.Offline;
using Plainspoken.Core.Settings;
using Plainspoken.Core.Transcription;

namespace Plainspoken.Core.Tests;

public sealed class OfflineTests : IDisposable
{
    private const string Content = "pretend whisper model bytes";
    private static readonly OfflineModel Model = new("test", "ggml-test.bin", "Test", Content.Length,
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Content))));

    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private static HttpResponseMessage Body(HttpStatusCode status, string body) => new(status) { Content = new StringContent(body) };

    [Fact]
    public async Task A_model_is_downloaded_checked_and_saved()
    {
        var handler = new FakeHttpHandler((_, _) => Body(HttpStatusCode.OK, Content));
        var store = new ModelStore(_dir.Path, new HttpClient(handler), new ListLog());
        long last = 0;
        await store.DownloadAsync(Model, (have, _) => last = have, default);
        Assert.True(store.IsDownloaded(Model));
        Assert.Equal(Content, File.ReadAllText(store.FileOf(Model)));
        Assert.Equal(Content.Length, last);
        Assert.Equal(OfflineModels.BaseUrl + "ggml-test.bin", handler.Requests.Single().Uri.ToString());
        store.Delete(Model);
        Assert.False(store.IsDownloaded(Model));
    }

    [Fact]
    public async Task A_partial_download_is_resumed_and_a_full_answer_to_the_resume_still_works()
    {
        File.WriteAllText(_dir.File("ggml-test.bin.part"), Content[..8]);
        var resumed = new FakeHttpHandler((r, _) =>
        {
            Assert.Equal("bytes=8-", r.Headers["Range"]);
            return Body(HttpStatusCode.PartialContent, Content[8..]);
        });
        await new ModelStore(_dir.Path, new HttpClient(resumed), new ListLog()).DownloadAsync(Model, null, default);
        Assert.Equal(Content, File.ReadAllText(_dir.File("ggml-test.bin")));

        File.Delete(_dir.File("ggml-test.bin"));
        File.WriteAllText(_dir.File("ggml-test.bin.part"), Content[..8]);
        await new ModelStore(_dir.Path, new HttpClient(new FakeHttpHandler((_, _) => Body(HttpStatusCode.OK, Content))), new ListLog())
            .DownloadAsync(Model, null, default);
        Assert.Equal(Content, File.ReadAllText(_dir.File("ggml-test.bin")));
    }

    [Fact]
    public async Task A_file_that_does_not_match_a_failed_request_or_an_unpinned_model_is_refused()
    {
        var store = new ModelStore(_dir.Path, new HttpClient(new FakeHttpHandler((_, _) => Body(HttpStatusCode.OK, "something else entirely!!!"))),
            new ListLog());
        await Assert.ThrowsAsync<IOException>(() => store.DownloadAsync(Model, null, default));
        Assert.False(store.IsDownloaded(Model));
        Assert.False(File.Exists(_dir.File("ggml-test.bin.part")));

        var failing = new ModelStore(_dir.Path, new HttpClient(new FakeHttpHandler((_, _) => Body(HttpStatusCode.NotFound, ""))), new ListLog());
        await Assert.ThrowsAsync<IOException>(() => failing.DownloadAsync(Model, null, default));
        await Assert.ThrowsAsync<IOException>(() => store.DownloadAsync(new OfflineModel("x", "x.bin", "X", 0, ""), null, default));
    }

    [Fact]
    public void Whisper_is_told_the_language_and_the_wanted_script()
    {
        TranscriptionRequest Request(LanguagePreset languages) =>
            new(WavEncoder.Encode(TestAudio.Speech(1)), 1, TranscriptionMode.Smart, languages, ["WhatsApp"]);
        Assert.Equal("en", OfflineWhisper.Language(Request(LanguagePreset.English)));
        Assert.Equal("auto", OfflineWhisper.Language(Request(LanguagePreset.EnglishHindi)));
        Assert.Contains("Kal meeting hai", OfflineWhisper.Prompt(Request(LanguagePreset.EnglishHindi)), StringComparison.Ordinal);
        Assert.Contains("WhatsApp", OfflineWhisper.Prompt(Request(LanguagePreset.EnglishHindi)), StringComparison.Ordinal);
        Assert.Equal("Kal milte hain.", OfflineWhisper.Tidy(" [BLANK_AUDIO]  Kal milte   hain. [Music]"));
        Assert.Equal(OfflineModels.Small, OfflineModels.Find(null, OfflineModels.Phone));
        Assert.Equal(OfflineModels.Medium, OfflineModels.Find("nope", OfflineModels.Pc));
    }
}
