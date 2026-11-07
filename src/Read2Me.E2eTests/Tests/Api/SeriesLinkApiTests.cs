using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Read2Me.E2eTests.Infrastructure;
using static Read2Me.E2eTests.Tests.Api.SeriesSeed;

namespace Read2Me.E2eTests.Tests.Api;

/// <summary>
/// Series characters and linking (#220) over HTTP: Share as new, Link, Unlink, the Link preview,
/// the series cast board read, <c>seriesLink</c> on <c>characters/summary</c>, and the status each
/// refusal maps to. Which links are refused is <c>SeriesLinkRulesTests</c>' to pin, and what each
/// side gains is <c>SeriesVoiceMergeTests</c>' and <c>GainSeriesVoicesMutationTests</c>'.
/// </summary>
public class SeriesLinkApiTests(E2eAppFixture app) : IClassFixture<E2eAppFixture>
{
    private readonly SeriesSeed seed = new(app);

    private Task<HttpResponseMessage> TryShareAsync(Guid series, string folder, Guid characterId) =>
        Http.PostAsJsonAsync(seed.Url($"/api/series/{series}/characters"), new { folder, characterId });

    private Task<HttpResponseMessage> UnlinkAsync(Guid series, Guid seriesCharacter, string folder) =>
        Http.DeleteAsync(seed.Url($"/api/series/{series}/characters/{seriesCharacter}/links/{folder}"));

    private Task<HttpResponseMessage> PreviewAsync(Guid series, Guid seriesCharacter, string folder, Guid characterId) =>
        Http.GetAsync(seed.Url($"/api/series/{series}/characters/{seriesCharacter}/links/{folder}/preview?characterId={characterId}"));

    private async Task<int> StoredSeriesCharacterCountAsync(Guid series)
    {
        await using var db = await seed.OpenRegistryAsync(series);
        return await db.Characters.CountAsync();
    }

    [Fact]
    public async Task Share_link_leave_and_unlink_over_HTTP_with_the_cast_read_and_the_copied_audio()
    {
        var holmes = await seed.SeriesAsync(books: 2);
        var (scarlet, four) = (holmes.Scarlet, holmes.Four);
        var armyDoctor = await app.SeedEditableVoiceAsync(scarlet.Folder, scarlet.Watson, "Army doctor");

        // Share: a row linked in its book, an empty cell with a suggestion and a picker elsewhere.
        var watson = await seed.ShareAsync(holmes.Series, scarlet);

        var cast = await seed.CastAsync(holmes.Series);
        Assert.Equal([scarlet.Folder, four.Folder], cast.GetProperty("books").EnumerateArray().Select(b => b.GetProperty("folder").GetString()));
        var row = Assert.Single(cast.GetProperty("rows").EnumerateArray());
        Assert.Equal(watson, row.GetProperty("seriesCharacterId").GetGuid());
        Assert.Equal("Watson", row.GetProperty("label").GetString());
        Assert.False(row.GetProperty("isNarrator").GetBoolean());
        var cells = row.GetProperty("cells").EnumerateArray().ToList();
        Assert.Equal(scarlet.Watson, cells[0].GetProperty("linked").GetProperty("characterId").GetGuid());
        Assert.False(cells[0].GetProperty("linked").GetProperty("differs").GetBoolean());
        Assert.Equal(JsonValueKind.Null, cells[1].GetProperty("linked").ValueKind);
        Assert.Equal(four.Watson, cells[1].GetProperty("suggested").GetProperty("characterId").GetGuid());
        var fourPicker = cast.GetProperty("unlinked").GetProperty(four.Folder).EnumerateArray().ToList();
        Assert.Contains(fourPicker, c => c.GetProperty("characterId").GetGuid() == four.Watson);
        Assert.Contains(fourPicker, c => c.GetProperty("isNarrator").GetBoolean());

        var link = (await seed.SummaryRowAsync(scarlet)).GetProperty("seriesLink");
        Assert.Equal(holmes.Series, link.GetProperty("seriesId").GetGuid());
        Assert.Equal(watson, link.GetProperty("seriesCharacterId").GetGuid());
        Assert.Equal(1, link.GetProperty("bookCount").GetInt32());
        Assert.Equal(JsonValueKind.Null, (await seed.SummaryRowAsync(four)).GetProperty("seriesLink").ValueKind);

        // The preview, then Link: a History step in the book that gains, and the reference audio copied under its Watson.
        var preview = await PreviewAsync(holmes.Series, watson, four.Folder, four.Watson);
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        Assert.Equal(["Army doctor"], (await JsonAsync(preview)).GetProperty("bookGains").EnumerateArray().Select(n => n.GetString()));

        await seed.LinkAsync(holmes.Series, watson, four);

        var audio = (await seed.VoiceAsync(four, armyDoctor))!.Value.GetProperty("audioFileName").GetString()!;
        Assert.StartsWith($"voices/{four.Watson}/", audio);
        Assert.True(File.Exists(Path.Combine(app.WorkspaceDir, four.Folder, audio)));
        Assert.Equal("Link series character", (await seed.HistoryLabelsAsync(four))[0]);
        row = Assert.Single((await seed.CastAsync(holmes.Series)).GetProperty("rows").EnumerateArray());
        Assert.All(row.GetProperty("cells").EnumerateArray(), c => Assert.NotEqual(JsonValueKind.Null, c.GetProperty("linked").ValueKind));
        Assert.Equal(2, (await seed.SummaryRowAsync(four)).GetProperty("seriesLink").GetProperty("bookCount").GetInt32());

        await AssertProblemAsync(await seed.TryLinkAsync(holmes.Series, watson, four.Folder, four.Watson), HttpStatusCode.Conflict, "already linked");

        // A leaving book takes its links, and a Series character left with none goes. No unit test
        // pins the registry applying SeriesLinkRules.OrphanedBy, so this stays here.
        await seed.ShareAsync(holmes.Series, four, four.Narrator);
        Assert.Equal(HttpStatusCode.NoContent, (await Http.DeleteAsync(seed.Url($"/api/series/{holmes.Series}/books/{four.Folder}"))).StatusCode);

        Assert.Equal(watson, Assert.Single((await seed.CastAsync(holmes.Series)).GetProperty("rows").EnumerateArray()).GetProperty("seriesCharacterId").GetGuid());
        Assert.Equal(1, await StoredSeriesCharacterCountAsync(holmes.Series));
        Assert.Equal(JsonValueKind.Null, (await seed.SummaryRowAsync(four)).GetProperty("seriesLink").ValueKind);
        Assert.NotNull(await seed.VoiceAsync(four, armyDoctor)); // the book keeps its copy

        // Unlinking the last link: the Series character goes.
        Assert.Equal(HttpStatusCode.NoContent, (await UnlinkAsync(holmes.Series, watson, scarlet.Folder)).StatusCode);

        Assert.Empty((await seed.CastAsync(holmes.Series)).GetProperty("rows").EnumerateArray());
        Assert.Equal(0, await StoredSeriesCharacterCountAsync(holmes.Series));
        Assert.Equal(JsonValueKind.Null, (await seed.SummaryRowAsync(scarlet)).GetProperty("seriesLink").ValueKind);
    }

    [Fact]
    public async Task Unknown_ids_and_books_outside_the_series_are_404_and_a_body_without_its_ids_400()
    {
        var holmes = await seed.SeriesAsync(books: 3, members: 2);
        var watson = await seed.ShareAsync(holmes.Series, holmes.Scarlet);
        var (four, outsider) = (holmes.Four, holmes.Adventures);

        await AssertProblemAsync(await TryShareAsync(holmes.Series, outsider.Folder, outsider.Watson), HttpStatusCode.NotFound, "The Adventures of Sherlock Holmes");
        await AssertProblemAsync(await seed.TryLinkAsync(holmes.Series, watson, outsider.Folder, outsider.Watson), HttpStatusCode.NotFound, "The Adventures of Sherlock Holmes");
        await AssertProblemAsync(await PreviewAsync(holmes.Series, watson, outsider.Folder, outsider.Watson), HttpStatusCode.NotFound, "The Adventures of Sherlock Holmes");
        await AssertProblemAsync(await UnlinkAsync(holmes.Series, watson, outsider.Folder), HttpStatusCode.NotFound, "The Adventures of Sherlock Holmes");
        await AssertProblemAsync(await seed.TryLinkAsync(holmes.Series, watson, "no-such-book", outsider.Watson), HttpStatusCode.NotFound, "no-such-book");

        Assert.Equal(HttpStatusCode.NotFound, (await Http.GetAsync(seed.Url($"/api/series/{Guid.NewGuid()}/cast"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await TryShareAsync(Guid.NewGuid(), four.Folder, four.Watson)).StatusCode);
        await AssertProblemAsync(await seed.TryLinkAsync(holmes.Series, Guid.NewGuid(), four.Folder, four.Watson), HttpStatusCode.NotFound, "series character");
        await AssertProblemAsync(await seed.TryLinkAsync(holmes.Series, watson, four.Folder, Guid.NewGuid()), HttpStatusCode.NotFound, "The Sign of the Four");
        await AssertProblemAsync(await TryShareAsync(holmes.Series, four.Folder, Guid.NewGuid()), HttpStatusCode.NotFound, "The Sign of the Four");
        await AssertProblemAsync(await UnlinkAsync(holmes.Series, watson, four.Folder), HttpStatusCode.NotFound, "The Sign of the Four", "Watson");

        Assert.Equal(HttpStatusCode.BadRequest, (await Http.PostAsJsonAsync(seed.Url($"/api/series/{holmes.Series}/characters"), new { folder = four.Folder })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Http.PutAsJsonAsync(seed.Url($"/api/series/{holmes.Series}/characters/{watson}/links/{four.Folder}"), new { })).StatusCode);
    }
}
