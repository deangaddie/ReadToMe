using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Read2Me.Data.Series;
using Read2Me.E2eTests.Infrastructure;

namespace Read2Me.E2eTests.Tests.Api;

/// <summary>
/// Link suggestions (#223, matching #212) on the series cast board read, <i>Not the same</i> in
/// both forms, and <i>Share and link</i>, over the sample series (#209): each book seeds Watson and
/// the seed Narrator row, both unlinked.
/// </summary>
public class SeriesSuggestionApiTests(E2eAppFixture app) : IClassFixture<E2eAppFixture>
{
    private static readonly HttpClient Http = new();

    private sealed record Book(string Folder, Guid Watson, Guid Narrator);

    private sealed record Holmes(Guid Series, Book Scarlet, Book Four);

    private async Task<Holmes> SeedAsync(string scarletWatson = "Watson", string fourWatson = "Watson")
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        async Task<Book> BookAsync(string folder, string title, string watson)
        {
            await app.SeedProjectAsync(folder, title, "Arthur Conan Doyle", watson);
            var rows = await GetJsonAsync($"/api/projects/{folder}/characters/summary");
            Guid IdOf(Func<JsonElement, bool> pick) => rows.EnumerateArray().Single(pick).GetProperty("id").GetGuid();
            return new Book(folder, IdOf(r => r.GetProperty("name").GetString() == watson), IdOf(r => r.GetProperty("isNarrator").GetBoolean()));
        }

        var scarlet = await BookAsync($"scarlet-{tag}", "A Study in Scarlet", scarletWatson);
        var four = await BookAsync($"four-{tag}", "The Sign of the Four", fourWatson);
        var created = await Http.PostAsJsonAsync($"{app.BaseUrl}/api/series", new { name = $"Sherlock Holmes {tag}", folder = scarlet.Folder });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var series = (await JsonAsync(created)).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await Http.PutAsJsonAsync($"{app.BaseUrl}/api/series/{series}/books/{four.Folder}", new { })).StatusCode);
        return new Holmes(series, scarlet, four);
    }

    private async Task<Guid> SharedAsync(Guid series, string folder, Guid characterId)
    {
        var response = await Http.PostAsJsonAsync($"{app.BaseUrl}/api/series/{series}/characters", new { folder, characterId });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await JsonAsync(response)).GetProperty("seriesCharacterId").GetGuid();
    }

    private Task<HttpResponseMessage> LinkAsync(Guid series, Guid seriesCharacter, string folder, Guid characterId) =>
        Http.PutAsJsonAsync($"{app.BaseUrl}/api/series/{series}/characters/{seriesCharacter}/links/{folder}", new { characterId });

    private Task<HttpResponseMessage> NotTheSameAsync(Guid series, object body) =>
        Http.PostAsJsonAsync($"{app.BaseUrl}/api/series/{series}/not-the-same", body);

    private Task<HttpResponseMessage> ShareAndLinkAsync(Guid series, params Book[] books) =>
        Http.PostAsJsonAsync($"{app.BaseUrl}/api/series/{series}/characters/share-and-link",
            new { members = books.Select(b => new { folder = b.Folder, characterId = b.Watson }) });

    private Task<JsonElement> CastAsync(Guid series) => GetJsonAsync($"/api/series/{series}/cast");

    private static JsonElement RowOf(JsonElement cast, Guid seriesCharacter) =>
        cast.GetProperty("rows").EnumerateArray().Single(r => r.GetProperty("seriesCharacterId").GetGuid() == seriesCharacter);

    private static JsonElement CellOf(JsonElement row, string folder) =>
        row.GetProperty("cells").EnumerateArray().Single(c => c.GetProperty("folder").GetString() == folder);

    private static List<string> SuggestedRowLabels(JsonElement cast) =>
        cast.GetProperty("suggestedRows").EnumerateArray().Select(r => r.GetProperty("label").GetString()!).ToList();

    private async Task RunAsync(string folder, object command)
    {
        var response = await Http.PostAsync($"{app.BaseUrl}/api/projects/{folder}/commands",
            new StringContent(JsonSerializer.Serialize(command), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private async Task<Guid> CreateVoiceAsync(string folder, Guid characterId, string name)
    {
        var response = await Http.PostAsync($"{app.BaseUrl}/api/projects/{folder}/commands", new StringContent(
            JsonSerializer.Serialize(new { type = "CreateVoice", characterId, name }), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await JsonAsync(response)).GetProperty("newEntityId").GetGuid();
    }

    private async Task<int> StoredNotTheSameAsync(Guid series)
    {
        await using var db = await SeriesDbContext.OpenAsync(Path.Combine(app.WorkspaceDir, ".series", series.ToString("D")));
        return await db.NotTheSame.CountAsync();
    }

    private async Task<JsonElement> GetJsonAsync(string path)
    {
        var response = await Http.GetAsync($"{app.BaseUrl}{path}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await JsonAsync(response);
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    // ---- suggestions on the board read ------------------------------------------------------------

    [Fact]
    public async Task Unlinked_matches_are_suggested_rows_and_a_shared_one_becomes_a_suggested_cell_that_goes_after_the_link()
    {
        var holmes = await SeedAsync(scarletWatson: "Watson", fourWatson: "Dr. Watson");

        var before = await CastAsync(holmes.Series);
        Assert.Equal(["Narrator", "Watson"], SuggestedRowLabels(before));
        Assert.Equal(2, before.GetProperty("suggestionCount").GetInt32());

        var watson = await SharedAsync(holmes.Series, holmes.Scarlet.Folder, holmes.Scarlet.Watson);
        var shared = await CastAsync(holmes.Series);
        var suggested = CellOf(RowOf(shared, watson), holmes.Four.Folder).GetProperty("suggested");
        Assert.Equal(holmes.Four.Watson, suggested.GetProperty("characterId").GetGuid());
        Assert.Equal("Dr. Watson", suggested.GetProperty("name").GetString());
        Assert.Equal([watson], shared.GetProperty("unlinked").GetProperty(holmes.Four.Folder).EnumerateArray()
            .Single(c => c.GetProperty("characterId").GetGuid() == holmes.Four.Watson)
            .GetProperty("nameMatches").EnumerateArray().Select(m => m.GetGuid()));
        Assert.Equal(["Narrator"], SuggestedRowLabels(shared));
        Assert.Equal(2, shared.GetProperty("suggestionCount").GetInt32());

        Assert.Equal(HttpStatusCode.NoContent, (await LinkAsync(holmes.Series, watson, holmes.Four.Folder, holmes.Four.Watson)).StatusCode);

        var linked = await CastAsync(holmes.Series);
        Assert.Equal(JsonValueKind.Null, CellOf(RowOf(linked, watson), holmes.Four.Folder).GetProperty("suggested").ValueKind);
        Assert.Equal(1, linked.GetProperty("suggestionCount").GetInt32());
    }

    [Fact]
    public async Task An_honorific_disagreement_suggests_nothing()
    {
        var holmes = await SeedAsync(scarletWatson: "Mr. Watson", fourWatson: "Mrs. Watson");

        var cast = await CastAsync(holmes.Series);

        Assert.Equal(["Narrator"], SuggestedRowLabels(cast));
    }

    // ---- Not the same ---------------------------------------------------------------------------

    [Fact]
    public async Task Not_the_same_on_a_cell_persists_until_that_Character_is_linked()
    {
        var holmes = await SeedAsync();
        var watson = await SharedAsync(holmes.Series, holmes.Scarlet.Folder, holmes.Scarlet.Watson);

        var dismissed = await NotTheSameAsync(holmes.Series,
            new { seriesCharacterId = watson, folder = holmes.Four.Folder, characterId = holmes.Four.Watson });

        Assert.Equal(HttpStatusCode.NoContent, dismissed.StatusCode);
        var cast = await CastAsync(holmes.Series);
        Assert.Equal(JsonValueKind.Null, CellOf(RowOf(cast, watson), holmes.Four.Folder).GetProperty("suggested").ValueKind);
        Assert.Equal(1, await StoredNotTheSameAsync(holmes.Series));
        // The picker still lists it, first, so the user can link it after all.
        Assert.Contains(cast.GetProperty("unlinked").GetProperty(holmes.Four.Folder).EnumerateArray(),
            c => c.GetProperty("characterId").GetGuid() == holmes.Four.Watson && c.GetProperty("nameMatches").GetArrayLength() == 1);

        Assert.Equal(HttpStatusCode.NoContent, (await LinkAsync(holmes.Series, watson, holmes.Four.Folder, holmes.Four.Watson)).StatusCode);

        Assert.Equal(0, await StoredNotTheSameAsync(holmes.Series));
    }

    [Fact]
    public async Task Not_the_same_on_a_row_records_each_pair_and_clears_when_a_Character_is_deleted()
    {
        var holmes = await SeedAsync();

        var dismissed = await NotTheSameAsync(holmes.Series, new
        {
            members = new[]
            {
                new { folder = holmes.Scarlet.Folder, characterId = holmes.Scarlet.Watson },
                new { folder = holmes.Four.Folder, characterId = holmes.Four.Watson },
            },
        });

        Assert.Equal(HttpStatusCode.NoContent, dismissed.StatusCode);
        var cast = await CastAsync(holmes.Series);
        Assert.Equal(["Narrator"], SuggestedRowLabels(cast));
        Assert.Equal(1, await StoredNotTheSameAsync(holmes.Series));

        await RunAsync(holmes.Four.Folder, new { type = "DeleteCharacter", characterId = holmes.Four.Watson });
        await CastAsync(holmes.Series);

        Assert.Equal(0, await StoredNotTheSameAsync(holmes.Series));
    }

    [Fact]
    public async Task A_body_that_is_neither_form_is_400()
    {
        var holmes = await SeedAsync();
        var watson = await SharedAsync(holmes.Series, holmes.Scarlet.Folder, holmes.Scarlet.Watson);
        var member = new { folder = holmes.Four.Folder, characterId = holmes.Four.Watson };

        Assert.Equal(HttpStatusCode.BadRequest, (await NotTheSameAsync(holmes.Series, new { })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await NotTheSameAsync(holmes.Series, new { seriesCharacterId = watson, folder = holmes.Four.Folder })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await NotTheSameAsync(holmes.Series,
            new { seriesCharacterId = watson, folder = holmes.Four.Folder, characterId = holmes.Four.Watson, members = new[] { member, member } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await NotTheSameAsync(holmes.Series, new { members = new[] { member } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await NotTheSameAsync(holmes.Series, new { members = new[] { member, member } })).StatusCode);
        Assert.Equal(0, await StoredNotTheSameAsync(holmes.Series));
    }

    [Fact]
    public async Task Not_the_same_on_a_linked_Character_is_409()
    {
        var holmes = await SeedAsync();
        var watson = await SharedAsync(holmes.Series, holmes.Scarlet.Folder, holmes.Scarlet.Watson);

        var response = await NotTheSameAsync(holmes.Series,
            new { seriesCharacterId = watson, folder = holmes.Scarlet.Folder, characterId = holmes.Scarlet.Watson });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    // ---- Share and link -------------------------------------------------------------------------

    [Fact]
    public async Task Share_and_link_links_every_member_and_its_preview_names_what_each_book_gains()
    {
        var holmes = await SeedAsync();
        var calm = await CreateVoiceAsync(holmes.Scarlet.Folder, holmes.Scarlet.Watson, "Calm");
        var brisk = await CreateVoiceAsync(holmes.Four.Folder, holmes.Four.Watson, "Brisk");

        var preview = await Http.PostAsJsonAsync($"{app.BaseUrl}/api/series/{holmes.Series}/characters/share-and-link/preview",
            new { members = new[] { new { folder = holmes.Four.Folder, characterId = holmes.Four.Watson }, new { folder = holmes.Scarlet.Folder, characterId = holmes.Scarlet.Watson } } });
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        var books = (await JsonAsync(preview)).GetProperty("books").EnumerateArray().ToList();
        Assert.Equal([holmes.Scarlet.Folder, holmes.Four.Folder], books.Select(b => b.GetProperty("folder").GetString()));
        Assert.Equal(["Brisk"], books[0].GetProperty("gains").EnumerateArray().Select(g => g.GetString()));
        Assert.Equal(["Calm"], books[1].GetProperty("gains").EnumerateArray().Select(g => g.GetString()));

        var response = await ShareAndLinkAsync(holmes.Series, holmes.Four, holmes.Scarlet);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var watson = (await JsonAsync(response)).GetProperty("seriesCharacterId").GetGuid();
        var row = RowOf(await CastAsync(holmes.Series), watson);
        Assert.Equal(holmes.Scarlet.Watson, CellOf(row, holmes.Scarlet.Folder).GetProperty("linked").GetProperty("characterId").GetGuid());
        Assert.Equal(holmes.Four.Watson, CellOf(row, holmes.Four.Folder).GetProperty("linked").GetProperty("characterId").GetGuid());
        foreach (var (folder, character) in new[] { (holmes.Scarlet.Folder, holmes.Scarlet.Watson), (holmes.Four.Folder, holmes.Four.Watson) })
        {
            var voices = (await GetJsonAsync($"/api/projects/{folder}/characters/{character}/voices"))
                .GetProperty("voices").EnumerateArray().Select(v => v.GetProperty("id").GetGuid()).ToHashSet();
            Assert.Superset(new HashSet<Guid> { calm, brisk }, voices);
        }
    }

    [Fact]
    public async Task Share_and_link_refuses_one_member_and_a_linked_Character()
    {
        var holmes = await SeedAsync();

        Assert.Equal(HttpStatusCode.BadRequest, (await ShareAndLinkAsync(holmes.Series, holmes.Four)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Http.PostAsJsonAsync(
            $"{app.BaseUrl}/api/series/{holmes.Series}/characters/share-and-link", new { })).StatusCode);

        await SharedAsync(holmes.Series, holmes.Scarlet.Folder, holmes.Scarlet.Watson);
        Assert.Equal(HttpStatusCode.Conflict, (await ShareAndLinkAsync(holmes.Series, holmes.Scarlet, holmes.Four)).StatusCode);
    }
}
