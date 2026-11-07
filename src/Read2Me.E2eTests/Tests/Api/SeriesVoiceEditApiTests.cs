using System.Net;
using Read2Me.E2eTests.Infrastructure;
using static Read2Me.E2eTests.Tests.Api.SeriesSeed;

namespace Read2Me.E2eTests.Tests.Api;

/// <summary>
/// Shared Voice edits (#221) over HTTP: an edit through one book's per-book routes is copied into
/// every other linked book in the same request, each copy a History step there, with its audio
/// files; a Restore stays local; a refused delete's 409 body; and the inline editor's read
/// <c>GET /api/series/{id}/characters/{scId}</c>. Which edits are copied and which deletes refused is
/// <c>SeriesVoiceCopyRulesTests</c>' and <c>SeriesVoiceCopierTests</c>' to pin; the default's move
/// <c>CopySeriesVoiceMutationTests</c>'.
/// </summary>
public class SeriesVoiceEditApiTests(E2eAppFixture app) : IClassFixture<E2eAppFixture>
{
    private readonly SeriesSeed seed = new(app);

    [Fact]
    public async Task Generated_audio_is_copied_into_each_book_under_its_own_Character_as_a_History_step_there()
    {
        var holmes = await seed.SeriesAsync(linkWatson: true);
        var voice = await seed.CreateVoiceAsync(holmes.Scarlet, "Army doctor", generated: true);
        await seed.RunAsync(holmes.Scarlet.Folder, new { type = "SetVoiceDesignPrompt", voiceId = voice, prompt = "A warm army doctor." });

        var generated = await Http.PostAsync(
            seed.Url($"/api/projects/{holmes.Scarlet.Folder}/characters/{holmes.Scarlet.Watson}/voices/{voice}/generate-audio"), null);
        Assert.Equal(HttpStatusCode.OK, generated.StatusCode);

        foreach (var book in new[] { holmes.Four, holmes.Adventures })
        {
            var copy = (await seed.VoiceAsync(book, voice))!.Value;
            Assert.Equal("A warm army doctor.", copy.GetProperty("designPrompt").GetString());
            var audio = copy.GetProperty("audioFileName").GetString()!;
            Assert.StartsWith($"voices/{book.Watson}/", audio);
            Assert.True(File.Exists(Path.Combine(app.WorkspaceDir, book.Folder, audio.Replace('/', Path.DirectorySeparatorChar))));
            Assert.Equal("Generate voice audio", (await seed.HistoryLabelsAsync(book))[0]);
        }
    }

    [Fact]
    public async Task A_Restore_stays_in_its_own_book()
    {
        var holmes = await seed.SeriesAsync(linkWatson: true);
        var voice = await seed.CreateVoiceAsync(holmes.Scarlet, "Army doctor");
        var before = await seed.HeadAsync(holmes.Scarlet);
        await seed.RunAsync(holmes.Scarlet.Folder, new { type = "UpdateVoice", voiceId = voice, name = "Old campaigner" });

        await seed.RestoreAsync(holmes.Scarlet, before);

        Assert.Equal("Army doctor", await seed.VoiceNameAsync(holmes.Scarlet, voice));
        Assert.Equal("Old campaigner", await seed.VoiceNameAsync(holmes.Four, voice));
        Assert.Equal("Old campaigner", await seed.VoiceNameAsync(holmes.Adventures, voice));
    }

    [Fact]
    public async Task A_refused_delete_is_a_409_naming_its_books_saves_nothing_and_the_editor_read_shows_it()
    {
        var holmes = await seed.SeriesAsync(chapters: true, linkWatson: true);
        var first = await seed.CreateVoiceAsync(holmes.Scarlet, "Army doctor");

        var last = await seed.CommandAsync(holmes.Scarlet.Folder, new { type = "DeleteVoice", voiceId = first });
        var lastBody = await AssertProblemAsync(last, HttpStatusCode.Conflict);
        Assert.Equal("LastVoice", lastBody.GetProperty("reason").GetString());
        Assert.Equal([holmes.Four.Folder, holmes.Adventures.Folder],
            lastBody.GetProperty("books").EnumerateArray().Select(b => b.GetProperty("folder").GetString()).ToArray());

        var second = await seed.CreateVoiceAsync(holmes.Scarlet, "Old campaigner");
        await seed.RunAsync(holmes.Four.Folder, new
        {
            type = "CreateVoiceRule", characterId = holmes.Four.Watson, voiceId = first, fromLevel = "Chapter", fromNodeId = holmes.Four.Chapter2,
        });

        var ruled = await seed.CommandAsync(holmes.Adventures.Folder, new { type = "DeleteVoice", voiceId = first });
        var ruledBody = await AssertProblemAsync(ruled, HttpStatusCode.Conflict, "The Sign of the Four");
        Assert.Equal("PositionalRules", ruledBody.GetProperty("reason").GetString());
        var blocker = Assert.Single(ruledBody.GetProperty("books").EnumerateArray());
        Assert.Equal(holmes.Four.Folder, blocker.GetProperty("folder").GetString());
        Assert.Equal(1, blocker.GetProperty("ruleCount").GetInt32());
        foreach (var book in holmes.Books)
            Assert.NotNull(await seed.VoiceAsync(book, first));

        var detail = await seed.GetJsonAsync($"/api/series/{holmes.Series}/characters/{holmes.SeriesWatson}");
        Assert.Equal(["Army doctor", "Old campaigner"],
            detail.GetProperty("voices").EnumerateArray().Select(v => v.GetProperty("name").GetString()).ToArray());
        Assert.Equal([holmes.Scarlet.Folder, holmes.Four.Folder, holmes.Adventures.Folder],
            detail.GetProperty("books").EnumerateArray().Select(b => b.GetProperty("folder").GetString()).ToArray());
        Assert.Equal("PositionalRules", detail.GetProperty("deleteBlocks").GetProperty(first.ToString()).GetProperty("reason").GetString());
        Assert.False(detail.GetProperty("deleteBlocks").TryGetProperty(second.ToString(), out _));

        Assert.Equal(HttpStatusCode.NotFound,
            (await Http.GetAsync(seed.Url($"/api/series/{holmes.Series}/characters/{Guid.NewGuid()}"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await Http.GetAsync(seed.Url($"/api/series/{Guid.NewGuid()}/characters/{holmes.SeriesWatson}"))).StatusCode);
    }
}
