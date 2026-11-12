using System.Net;
using System.Net.Http.Json;
using Read2Me.E2eTests.Infrastructure;
using static Read2Me.E2eTests.Tests.Api.SeriesSeed;

namespace Read2Me.E2eTests.Tests.Api;

/// <summary>
/// Differs from series (#222) over HTTP: a Restore to older Voices makes its book differ, as the
/// board, the editor read and <c>seriesLink</c> show, and push and take resolve it, each a History
/// step, with the status each refusal maps to. Which version is the series copy is
/// <c>SeriesCopyChoiceTests</c>' to pin, what a push or take loses <c>SeriesVoiceLossesTests</c>', and
/// how an edit brings a book back in line <c>SeriesVoiceCopierTests</c>' and <c>SeriesSnapshotTests</c>'.
/// </summary>
public class SeriesDiffersApiTests(E2eAppFixture app) : IClassFixture<E2eAppFixture>
{
    private readonly SeriesSeed seed = new(app);

    private Task<HttpResponseMessage> ResolveAsync(Holmes holmes, string how, Book book) =>
        Http.PostAsJsonAsync(seed.Url($"/api/series/{holmes.Series}/characters/{holmes.SeriesWatson}/{how}"), new { folder = book.Folder });

    private Task<HttpResponseMessage> PreviewAsync(Holmes holmes, string how, Book book) =>
        Http.GetAsync(seed.Url($"/api/series/{holmes.Series}/characters/{holmes.SeriesWatson}/{how}/preview?folder={book.Folder}"));

    private async Task<Dictionary<string, bool>> BoardDiffersAsync(Holmes holmes)
    {
        var row = Assert.Single((await seed.CastAsync(holmes.Series)).GetProperty("rows").EnumerateArray());
        return row.GetProperty("cells").EnumerateArray().ToDictionary(
            c => c.GetProperty("folder").GetString()!, c => c.GetProperty("linked").GetProperty("differs").GetBoolean());
    }

    private async Task<bool> LinkDiffersAsync(Book book) =>
        (await seed.SummaryRowAsync(book)).GetProperty("seriesLink").GetProperty("differs").GetBoolean();

    /// <summary>Three linked books sharing a Voice "Army doctor", renamed "Old campaigner" in every book, then Scarlet Restored to before the rename.</summary>
    private async Task<(Holmes Holmes, Guid Voice)> SeedRestoredAsync()
    {
        var holmes = await seed.SeriesAsync(linkWatson: true);
        var voice = await seed.CreateVoiceAsync(holmes.Scarlet, "Army doctor");
        var before = await seed.HeadAsync(holmes.Scarlet);
        await seed.RunAsync(holmes.Four.Folder, new { type = "UpdateVoice", voiceId = voice, name = "Old campaigner", description = (string?)null });
        await seed.RestoreAsync(holmes.Scarlet, before);
        return (holmes, voice);
    }

    [Fact]
    public async Task A_Restore_makes_its_book_differ_on_the_board_the_summary_and_the_editor_read()
    {
        var (holmes, voice) = await SeedRestoredAsync();

        Assert.Equal(
            new Dictionary<string, bool> { [holmes.Scarlet.Folder] = true, [holmes.Four.Folder] = false, [holmes.Adventures.Folder] = false },
            await BoardDiffersAsync(holmes));
        Assert.True(await LinkDiffersAsync(holmes.Scarlet));
        Assert.False(await LinkDiffersAsync(holmes.Four));

        var detail = await seed.GetJsonAsync($"/api/series/{holmes.Series}/characters/{holmes.SeriesWatson}");
        Assert.Equal(holmes.Four.Folder, detail.GetProperty("seriesCopyFolder").GetString());
        Assert.Equal([true, false, false], detail.GetProperty("books").EnumerateArray().Select(b => b.GetProperty("differs").GetBoolean()).ToArray());
        Assert.Equal("Army doctor", await seed.VoiceNameAsync(holmes.Scarlet, voice));
    }

    [Fact]
    public async Task Push_and_take_copy_as_a_History_step_and_refuse_a_book_that_doesnt_differ()
    {
        var (holmes, voice) = await SeedRestoredAsync();

        // Refusals first: nothing differs but Scarlet, and the ids must be known.
        Assert.Equal(HttpStatusCode.Conflict, (await ResolveAsync(holmes, "push", holmes.Adventures)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await ResolveAsync(holmes, "take", holmes.Four)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await PreviewAsync(holmes, "take", holmes.Four)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Http.PostAsJsonAsync(
            seed.Url($"/api/series/{holmes.Series}/characters/{Guid.NewGuid()}/push"), new { folder = holmes.Scarlet.Folder })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Http.PostAsJsonAsync(
            seed.Url($"/api/series/{holmes.Series}/characters/{holmes.SeriesWatson}/take"), new { folder = "no-such-book" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Http.PostAsJsonAsync(
            seed.Url($"/api/series/{holmes.Series}/characters/{holmes.SeriesWatson}/take"), new { })).StatusCode);

        // Push Scarlet's Voices: a step in each other book, none in Scarlet.
        Assert.Equal(HttpStatusCode.OK, (await PreviewAsync(holmes, "push", holmes.Scarlet)).StatusCode);
        var fourBeforePush = await seed.HeadAsync(holmes.Four);
        Assert.Equal(HttpStatusCode.NoContent, (await ResolveAsync(holmes, "push", holmes.Scarlet)).StatusCode);

        foreach (var book in holmes.Books)
            Assert.Equal("Army doctor", await seed.VoiceNameAsync(book, voice));
        Assert.Equal("Push series voices", (await seed.HistoryLabelsAsync(holmes.Four))[0]);
        Assert.Equal("Push series voices", (await seed.HistoryLabelsAsync(holmes.Adventures))[0]);
        Assert.NotEqual("Push series voices", (await seed.HistoryLabelsAsync(holmes.Scarlet))[0]);
        Assert.All((await BoardDiffersAsync(holmes)).Values, Assert.False);

        // Restore Four to before the push, so it differs, and take the series copy into it: a step there only.
        await seed.RestoreAsync(holmes.Four, fourBeforePush);
        Assert.True((await BoardDiffersAsync(holmes))[holmes.Four.Folder]);
        Assert.Equal(HttpStatusCode.OK, (await PreviewAsync(holmes, "take", holmes.Four)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await ResolveAsync(holmes, "take", holmes.Four)).StatusCode);

        Assert.Equal("Army doctor", await seed.VoiceNameAsync(holmes.Four, voice));
        Assert.Equal("Take series voices", (await seed.HistoryLabelsAsync(holmes.Four))[0]);
        Assert.Equal("Push series voices", (await seed.HistoryLabelsAsync(holmes.Adventures))[0]);
        Assert.All((await BoardDiffersAsync(holmes)).Values, Assert.False);
    }
}
