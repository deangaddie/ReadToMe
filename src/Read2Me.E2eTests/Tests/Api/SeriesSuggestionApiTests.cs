using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Read2Me.E2eTests.Infrastructure;
using static Read2Me.E2eTests.Tests.Api.SeriesSeed;

namespace Read2Me.E2eTests.Tests.Api;

/// <summary>
/// <i>Not the same</i> in both forms and <i>Share and link</i> (#223) over HTTP, with the status each
/// refusal maps to. Which Characters are suggested is <c>SeriesNameMatchTests</c>' to pin, and which
/// dismissals hide or go <c>SeriesSnapshotTests</c>' and <c>SeriesRegistryTests</c>'. Each book seeds
/// Watson and the seed Narrator row, both unlinked.
/// </summary>
public class SeriesSuggestionApiTests(E2eAppFixture app) : IClassFixture<E2eAppFixture>
{
    private readonly SeriesSeed seed = new(app);

    private Task<HttpResponseMessage> NotTheSameAsync(Guid series, object body) =>
        Http.PostAsJsonAsync(seed.Url($"/api/series/{series}/not-the-same"), body);

    private static object Members(params Book[] books) =>
        new { members = books.Select(b => new { folder = b.Folder, characterId = b.Watson }) };

    private Task<HttpResponseMessage> ShareAndLinkAsync(Guid series, params Book[] books) =>
        Http.PostAsJsonAsync(seed.Url($"/api/series/{series}/characters/share-and-link"), Members(books));

    private static JsonElement CellOf(JsonElement cast, Guid seriesCharacter, string folder) =>
        cast.GetProperty("rows").EnumerateArray().Single(r => r.GetProperty("seriesCharacterId").GetGuid() == seriesCharacter)
            .GetProperty("cells").EnumerateArray().Single(c => c.GetProperty("folder").GetString() == folder);

    private async Task<int> StoredNotTheSameAsync(Guid series)
    {
        await using var db = await seed.OpenRegistryAsync(series);
        return await db.NotTheSame.CountAsync();
    }

    [Fact]
    public async Task Not_the_same_records_a_cell_or_a_row_and_refuses_a_body_of_neither_form_or_a_linked_Character()
    {
        var holmes = await seed.SeriesAsync(books: 2);
        var (scarlet, four) = (holmes.Scarlet, holmes.Four);
        var watson = await seed.ShareAsync(holmes.Series, scarlet);
        var member = new { folder = four.Folder, characterId = four.Watson };

        Assert.Equal(HttpStatusCode.BadRequest, (await NotTheSameAsync(holmes.Series, new { })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await NotTheSameAsync(holmes.Series, new { seriesCharacterId = watson, folder = four.Folder })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await NotTheSameAsync(holmes.Series,
            new { seriesCharacterId = watson, folder = four.Folder, characterId = four.Watson, members = new[] { member, member } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await NotTheSameAsync(holmes.Series, new { members = new[] { member } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await NotTheSameAsync(holmes.Series, new { members = new[] { member, member } })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await NotTheSameAsync(holmes.Series,
            new { seriesCharacterId = watson, folder = scarlet.Folder, characterId = scarlet.Watson })).StatusCode);
        Assert.Equal(0, await StoredNotTheSameAsync(holmes.Series));

        // On a cell: the suggestion goes from the board read.
        Assert.Equal(HttpStatusCode.NoContent, (await NotTheSameAsync(holmes.Series,
            new { seriesCharacterId = watson, folder = four.Folder, characterId = four.Watson })).StatusCode);
        Assert.Equal(JsonValueKind.Null, CellOf(await seed.CastAsync(holmes.Series), watson, four.Folder).GetProperty("suggested").ValueKind);
        Assert.Equal(1, await StoredNotTheSameAsync(holmes.Series));

        // On a row: the two Narrators.
        var narrators = new { members = new[] { new { folder = scarlet.Folder, characterId = scarlet.Narrator }, new { folder = four.Folder, characterId = four.Narrator } } };
        Assert.Equal(HttpStatusCode.NoContent, (await NotTheSameAsync(holmes.Series, narrators)).StatusCode);
        Assert.Empty((await seed.CastAsync(holmes.Series)).GetProperty("suggestedRows").EnumerateArray());
        Assert.Equal(2, await StoredNotTheSameAsync(holmes.Series));
    }

    [Fact]
    public async Task Share_and_link_links_every_member_after_its_preview_and_refuses_one_member_or_a_linked_Character()
    {
        var holmes = await seed.SeriesAsync(books: 2);
        var (scarlet, four) = (holmes.Scarlet, holmes.Four);
        await seed.CreateVoiceAsync(scarlet, "Calm");
        await seed.CreateVoiceAsync(four, "Brisk");

        Assert.Equal(HttpStatusCode.BadRequest, (await ShareAndLinkAsync(holmes.Series, four)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Http.PostAsJsonAsync(
            seed.Url($"/api/series/{holmes.Series}/characters/share-and-link"), new { })).StatusCode);

        var preview = await Http.PostAsJsonAsync(seed.Url($"/api/series/{holmes.Series}/characters/share-and-link/preview"), Members(four, scarlet));
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        var books = (await JsonAsync(preview)).GetProperty("books").EnumerateArray().ToList();
        Assert.Equal([scarlet.Folder, four.Folder], books.Select(b => b.GetProperty("folder").GetString()));
        Assert.Equal(["Brisk"], books[0].GetProperty("gains").EnumerateArray().Select(g => g.GetString()));
        Assert.Equal(["Calm"], books[1].GetProperty("gains").EnumerateArray().Select(g => g.GetString()));

        var response = await ShareAndLinkAsync(holmes.Series, four, scarlet);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var watson = (await JsonAsync(response)).GetProperty("seriesCharacterId").GetGuid();
        var cast = await seed.CastAsync(holmes.Series);
        Assert.Equal(scarlet.Watson, CellOf(cast, watson, scarlet.Folder).GetProperty("linked").GetProperty("characterId").GetGuid());
        Assert.Equal(four.Watson, CellOf(cast, watson, four.Folder).GetProperty("linked").GetProperty("characterId").GetGuid());

        Assert.Equal(HttpStatusCode.Conflict, (await ShareAndLinkAsync(holmes.Series, scarlet, four)).StatusCode);
    }
}
