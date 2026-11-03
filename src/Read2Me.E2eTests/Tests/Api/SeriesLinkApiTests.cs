using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Read2Me.Data.Series;
using Read2Me.E2eTests.Infrastructure;

namespace Read2Me.E2eTests.Tests.Api;

/// <summary>
/// Series characters and linking (#220): Share as new, Link with its Voice merge, Unlink, the Link
/// preview, the series cast board read and <c>seriesLink</c> on <c>characters/summary</c>, with every
/// refusal the Series Agent API (#213) lists. The books are the sample series (#209).
/// </summary>
public class SeriesLinkApiTests(E2eAppFixture app) : IClassFixture<E2eAppFixture>
{
    private static readonly HttpClient Http = new();

    private sealed record Book(string Folder, Guid Watson, Guid Narrator);

    private sealed record Holmes(Guid Series, Book Scarlet, Book Four, Book Adventures);

    /// <summary>Three Holmes books, each with Watson; Scarlet and Four in a fresh series, Adventures in none.</summary>
    private async Task<Holmes> SeedAsync()
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        async Task<Book> BookAsync(string folder, string title)
        {
            await app.SeedProjectAsync(folder, title, "Arthur Conan Doyle", "Watson");
            var rows = await GetJsonAsync($"/api/projects/{folder}/characters/summary");
            Guid IdOf(Func<JsonElement, bool> pick) => rows.EnumerateArray().Single(pick).GetProperty("id").GetGuid();
            return new Book(folder, IdOf(r => r.GetProperty("name").GetString() == "Watson"), IdOf(r => r.GetProperty("isNarrator").GetBoolean()));
        }

        var scarlet = await BookAsync($"scarlet-{tag}", "A Study in Scarlet");
        var four = await BookAsync($"four-{tag}", "The Sign of the Four");
        var adventures = await BookAsync($"adventures-{tag}", "The Adventures of Sherlock Holmes");

        var created = await Http.PostAsJsonAsync($"{app.BaseUrl}/api/series", new { name = $"Sherlock Holmes {tag}", folder = scarlet.Folder });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var series = (await JsonAsync(created)).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await Http.PutAsJsonAsync($"{app.BaseUrl}/api/series/{series}/books/{four.Folder}", new { })).StatusCode);
        return new Holmes(series, scarlet, four, adventures);
    }

    private Task<HttpResponseMessage> ShareAsync(Guid series, string folder, Guid characterId) =>
        Http.PostAsJsonAsync($"{app.BaseUrl}/api/series/{series}/characters", new { folder, characterId });

    private async Task<Guid> SharedAsync(Guid series, string folder, Guid characterId)
    {
        var response = await ShareAsync(series, folder, characterId);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await JsonAsync(response)).GetProperty("seriesCharacterId").GetGuid();
    }

    private Task<HttpResponseMessage> LinkAsync(Guid series, Guid seriesCharacter, string folder, Guid characterId) =>
        Http.PutAsJsonAsync($"{app.BaseUrl}/api/series/{series}/characters/{seriesCharacter}/links/{folder}", new { characterId });

    private Task<HttpResponseMessage> UnlinkAsync(Guid series, Guid seriesCharacter, string folder) =>
        Http.DeleteAsync($"{app.BaseUrl}/api/series/{series}/characters/{seriesCharacter}/links/{folder}");

    private Task<HttpResponseMessage> PreviewAsync(Guid series, Guid seriesCharacter, string folder, Guid characterId) =>
        Http.GetAsync($"{app.BaseUrl}/api/series/{series}/characters/{seriesCharacter}/links/{folder}/preview?characterId={characterId}");

    private Task<JsonElement> CastAsync(Guid series) => GetJsonAsync($"/api/series/{series}/cast");

    private async Task<Guid> CreateVoiceAsync(string folder, Guid characterId, string name)
    {
        var response = await Http.PostAsync($"{app.BaseUrl}/api/projects/{folder}/commands", new StringContent(
            JsonSerializer.Serialize(new { type = "CreateVoice", characterId, name }), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await JsonAsync(response)).GetProperty("newEntityId").GetGuid();
    }

    /// <summary>A Character's Voices in this book, by Id, with the audio each names.</summary>
    private async Task<Dictionary<Guid, (string Name, string? Audio)>> VoicesAsync(string folder, Guid characterId)
    {
        var voices = await GetJsonAsync($"/api/projects/{folder}/characters/{characterId}/voices");
        return voices.GetProperty("voices").EnumerateArray()
            .ToDictionary(
                v => v.GetProperty("id").GetGuid(),
                v => (v.GetProperty("name").GetString()!,
                      v.TryGetProperty("audioFileName", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString() : null));
    }

    private async Task<JsonElement> SummaryRowAsync(string folder, Guid characterId) =>
        (await GetJsonAsync($"/api/projects/{folder}/characters/summary")).EnumerateArray()
            .Single(r => r.GetProperty("id").GetGuid() == characterId);

    private async Task<IReadOnlyList<string>> HistoryLabelsAsync(string folder) =>
        (await GetJsonAsync($"/api/projects/{folder}/history")).GetProperty("steps").EnumerateArray()
            .Select(s => s.GetProperty("label").GetString()!).ToList();

    private async Task<int> StoredSeriesCharacterCountAsync(Guid series)
    {
        await using var db = await SeriesDbContext.OpenAsync(Path.Combine(app.WorkspaceDir, ".series", series.ToString("D")));
        return await db.Characters.CountAsync();
    }

    private async Task<JsonElement> GetJsonAsync(string path)
    {
        var response = await Http.GetAsync($"{app.BaseUrl}{path}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await JsonAsync(response);
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, params string[] named)
    {
        Assert.Equal(status, response.StatusCode);
        var detail = (await JsonAsync(response)).GetProperty("detail").GetString();
        foreach (var name in named)
            Assert.Contains(name, detail);
    }

    // ---- share, the board, the summary ----------------------------------------------------------

    [Fact]
    public async Task Sharing_a_Character_makes_a_row_linked_in_its_book_with_empty_cells_and_pickers_elsewhere()
    {
        var holmes = await SeedAsync();

        var watson = await SharedAsync(holmes.Series, holmes.Scarlet.Folder, holmes.Scarlet.Watson);

        var cast = await CastAsync(holmes.Series);
        Assert.Equal([holmes.Scarlet.Folder, holmes.Four.Folder],
            cast.GetProperty("books").EnumerateArray().Select(b => b.GetProperty("folder").GetString()));
        var row = Assert.Single(cast.GetProperty("rows").EnumerateArray());
        Assert.Equal(watson, row.GetProperty("seriesCharacterId").GetGuid());
        Assert.Equal("Watson", row.GetProperty("label").GetString());
        Assert.False(row.GetProperty("isNarrator").GetBoolean());
        var cells = row.GetProperty("cells").EnumerateArray().ToList();
        Assert.Equal(holmes.Scarlet.Watson, cells[0].GetProperty("linked").GetProperty("characterId").GetGuid());
        Assert.False(cells[0].GetProperty("linked").GetProperty("differs").GetBoolean());
        Assert.Equal(JsonValueKind.Null, cells[1].GetProperty("linked").ValueKind);
        Assert.Equal(JsonValueKind.Null, cells[1].GetProperty("suggested").ValueKind);

        // The pickers: each book's unlinked Characters, the Narrator among them; no suggestions yet.
        var unlinked = cast.GetProperty("unlinked");
        Assert.DoesNotContain(holmes.Scarlet.Watson, unlinked.GetProperty(holmes.Scarlet.Folder).EnumerateArray().Select(c => c.GetProperty("characterId").GetGuid()));
        var fourPicker = unlinked.GetProperty(holmes.Four.Folder).EnumerateArray().ToList();
        Assert.Contains(fourPicker, c => c.GetProperty("characterId").GetGuid() == holmes.Four.Watson);
        Assert.Contains(fourPicker, c => c.GetProperty("isNarrator").GetBoolean());
        Assert.All(fourPicker, c => Assert.Empty(c.GetProperty("nameMatches").EnumerateArray()));
        Assert.Empty(cast.GetProperty("suggestedRows").EnumerateArray());
        Assert.Equal(0, cast.GetProperty("suggestionCount").GetInt32());

        var summary = await SummaryRowAsync(holmes.Scarlet.Folder, holmes.Scarlet.Watson);
        var link = summary.GetProperty("seriesLink");
        Assert.Equal(holmes.Series, link.GetProperty("seriesId").GetGuid());
        Assert.Equal(watson, link.GetProperty("seriesCharacterId").GetGuid());
        Assert.Equal(1, link.GetProperty("bookCount").GetInt32());
        Assert.False(link.GetProperty("differs").GetBoolean());
        Assert.Equal(JsonValueKind.Null, (await SummaryRowAsync(holmes.Four.Folder, holmes.Four.Watson)).GetProperty("seriesLink").ValueKind);
    }

    // ---- link: the Voice merge --------------------------------------------------------------------

    [Fact]
    public async Task Linking_gives_each_side_the_others_Voices_under_the_same_Ids_as_a_History_step_in_each_book()
    {
        var holmes = await SeedAsync();
        var armyDoctor = await app.SeedEditableVoiceAsync(holmes.Scarlet.Folder, holmes.Scarlet.Watson, "Army doctor");
        var biographer = await CreateVoiceAsync(holmes.Four.Folder, holmes.Four.Watson, "Biographer");
        var watson = await SharedAsync(holmes.Series, holmes.Scarlet.Folder, holmes.Scarlet.Watson);

        var preview = await PreviewAsync(holmes.Series, watson, holmes.Four.Folder, holmes.Four.Watson);
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        var gains = await JsonAsync(preview);
        Assert.Equal(["Army doctor"], gains.GetProperty("bookGains").EnumerateArray().Select(n => n.GetString()));
        Assert.Equal(["Biographer"], gains.GetProperty("seriesGains").EnumerateArray().Select(n => n.GetString()));

        Assert.Equal(HttpStatusCode.NoContent, (await LinkAsync(holmes.Series, watson, holmes.Four.Folder, holmes.Four.Watson)).StatusCode);

        var inFour = await VoicesAsync(holmes.Four.Folder, holmes.Four.Watson);
        var inScarlet = await VoicesAsync(holmes.Scarlet.Folder, holmes.Scarlet.Watson);
        Assert.Equal(new[] { armyDoctor, biographer }.Order(), inFour.Keys.Order());
        Assert.Equal(inFour.Keys.Order(), inScarlet.Keys.Order());
        Assert.Equal("Army doctor", inFour[armyDoctor].Name);
        // The reference audio is copied under Four's Watson.
        var audio = inFour[armyDoctor].Audio!;
        Assert.StartsWith($"voices/{holmes.Four.Watson}/", audio);
        Assert.True(File.Exists(Path.Combine(app.WorkspaceDir, holmes.Four.Folder, audio)));

        Assert.Equal("Link series character", (await HistoryLabelsAsync(holmes.Four.Folder))[0]);
        Assert.Equal("Link series character", (await HistoryLabelsAsync(holmes.Scarlet.Folder))[0]);

        var cast = await CastAsync(holmes.Series);
        var row = Assert.Single(cast.GetProperty("rows").EnumerateArray());
        Assert.All(row.GetProperty("cells").EnumerateArray(), c => Assert.NotEqual(JsonValueKind.Null, c.GetProperty("linked").ValueKind));
        Assert.Equal(2, (await SummaryRowAsync(holmes.Four.Folder, holmes.Four.Watson)).GetProperty("seriesLink").GetProperty("bookCount").GetInt32());
    }

    [Fact]
    public async Task The_Narrator_row_is_shared_and_linked_like_any_other()
    {
        var holmes = await SeedAsync();
        var narration = await CreateVoiceAsync(holmes.Scarlet.Folder, holmes.Scarlet.Narrator, "Narration");
        var narrator = await SharedAsync(holmes.Series, holmes.Scarlet.Folder, holmes.Scarlet.Narrator);

        Assert.Equal(HttpStatusCode.NoContent, (await LinkAsync(holmes.Series, narrator, holmes.Four.Folder, holmes.Four.Narrator)).StatusCode);

        Assert.Contains(narration, (await VoicesAsync(holmes.Four.Folder, holmes.Four.Narrator)).Keys);
        var row = Assert.Single((await CastAsync(holmes.Series)).GetProperty("rows").EnumerateArray());
        Assert.True(row.GetProperty("isNarrator").GetBoolean());
    }

    // ---- refusals --------------------------------------------------------------------------------

    [Fact]
    public async Task A_book_not_in_the_series_is_404_on_every_route()
    {
        var holmes = await SeedAsync();
        var watson = await SharedAsync(holmes.Series, holmes.Scarlet.Folder, holmes.Scarlet.Watson);
        var outsider = holmes.Adventures;

        await AssertProblemAsync(await ShareAsync(holmes.Series, outsider.Folder, outsider.Watson), HttpStatusCode.NotFound, "The Adventures of Sherlock Holmes");
        await AssertProblemAsync(await LinkAsync(holmes.Series, watson, outsider.Folder, outsider.Watson), HttpStatusCode.NotFound, "The Adventures of Sherlock Holmes");
        await AssertProblemAsync(await PreviewAsync(holmes.Series, watson, outsider.Folder, outsider.Watson), HttpStatusCode.NotFound, "The Adventures of Sherlock Holmes");
        await AssertProblemAsync(await UnlinkAsync(holmes.Series, watson, outsider.Folder), HttpStatusCode.NotFound, "The Adventures of Sherlock Holmes");
        await AssertProblemAsync(await LinkAsync(holmes.Series, watson, "no-such-book", outsider.Watson), HttpStatusCode.NotFound, "no-such-book");
    }

    [Fact]
    public async Task An_unknown_series_Series_character_or_Character_is_404()
    {
        var holmes = await SeedAsync();
        var watson = await SharedAsync(holmes.Series, holmes.Scarlet.Folder, holmes.Scarlet.Watson);

        Assert.Equal(HttpStatusCode.NotFound, (await Http.GetAsync($"{app.BaseUrl}/api/series/{Guid.NewGuid()}/cast")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ShareAsync(Guid.NewGuid(), holmes.Four.Folder, holmes.Four.Watson)).StatusCode);
        await AssertProblemAsync(await LinkAsync(holmes.Series, Guid.NewGuid(), holmes.Four.Folder, holmes.Four.Watson), HttpStatusCode.NotFound, "series character");
        await AssertProblemAsync(await LinkAsync(holmes.Series, watson, holmes.Four.Folder, Guid.NewGuid()), HttpStatusCode.NotFound, "The Sign of the Four");
        await AssertProblemAsync(await ShareAsync(holmes.Series, holmes.Four.Folder, Guid.NewGuid()), HttpStatusCode.NotFound, "The Sign of the Four");
        await AssertProblemAsync(await UnlinkAsync(holmes.Series, watson, holmes.Four.Folder), HttpStatusCode.NotFound, "The Sign of the Four", "Watson");
    }

    [Fact]
    public async Task An_already_linked_Character_is_409_and_so_is_a_second_Character_of_one_book()
    {
        var holmes = await SeedAsync();
        var watson = await SharedAsync(holmes.Series, holmes.Scarlet.Folder, holmes.Scarlet.Watson);
        Assert.Equal(HttpStatusCode.NoContent, (await LinkAsync(holmes.Series, watson, holmes.Four.Folder, holmes.Four.Watson)).StatusCode);

        await AssertProblemAsync(await ShareAsync(holmes.Series, holmes.Four.Folder, holmes.Four.Watson), HttpStatusCode.Conflict, "Watson", "already linked");
        await AssertProblemAsync(await LinkAsync(holmes.Series, watson, holmes.Four.Folder, holmes.Four.Watson), HttpStatusCode.Conflict, "already linked");

        // The Narrator of Four as a second Character for Watson's row: merge first.
        await AssertProblemAsync(await LinkAsync(holmes.Series, watson, holmes.Four.Folder, holmes.Four.Narrator), HttpStatusCode.Conflict,
            "The Sign of the Four", "Watson", "merge");
    }

    [Fact]
    public async Task A_body_without_its_ids_is_400()
    {
        var holmes = await SeedAsync();
        var watson = await SharedAsync(holmes.Series, holmes.Scarlet.Folder, holmes.Scarlet.Watson);

        Assert.Equal(HttpStatusCode.BadRequest, (await Http.PostAsJsonAsync($"{app.BaseUrl}/api/series/{holmes.Series}/characters", new { folder = holmes.Four.Folder })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Http.PutAsJsonAsync($"{app.BaseUrl}/api/series/{holmes.Series}/characters/{watson}/links/{holmes.Four.Folder}", new { })).StatusCode);
    }

    // ---- unlink, leave, the Series character's end -------------------------------------------------

    [Fact]
    public async Task Unlinking_keeps_the_books_copy_and_the_last_link_going_removes_the_Series_character()
    {
        var holmes = await SeedAsync();
        var armyDoctor = await CreateVoiceAsync(holmes.Scarlet.Folder, holmes.Scarlet.Watson, "Army doctor");
        var watson = await SharedAsync(holmes.Series, holmes.Scarlet.Folder, holmes.Scarlet.Watson);
        Assert.Equal(HttpStatusCode.NoContent, (await LinkAsync(holmes.Series, watson, holmes.Four.Folder, holmes.Four.Watson)).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await UnlinkAsync(holmes.Series, watson, holmes.Four.Folder)).StatusCode);

        Assert.Contains(armyDoctor, (await VoicesAsync(holmes.Four.Folder, holmes.Four.Watson)).Keys);
        Assert.Equal(JsonValueKind.Null, (await SummaryRowAsync(holmes.Four.Folder, holmes.Four.Watson)).GetProperty("seriesLink").ValueKind);
        Assert.Single((await CastAsync(holmes.Series)).GetProperty("rows").EnumerateArray());

        Assert.Equal(HttpStatusCode.NoContent, (await UnlinkAsync(holmes.Series, watson, holmes.Scarlet.Folder)).StatusCode);

        Assert.Empty((await CastAsync(holmes.Series)).GetProperty("rows").EnumerateArray());
        Assert.Equal(0, await StoredSeriesCharacterCountAsync(holmes.Series));
    }

    [Fact]
    public async Task A_member_that_cant_be_read_keeps_its_links_for_a_later_read()
    {
        var holmes = await SeedAsync();
        var watson = await SharedAsync(holmes.Series, holmes.Scarlet.Folder, holmes.Scarlet.Watson);
        Assert.Equal(HttpStatusCode.NoContent, (await LinkAsync(holmes.Series, watson, holmes.Four.Folder, holmes.Four.Watson)).StatusCode);
        var fourFolder = Path.Combine(app.WorkspaceDir, holmes.Four.Folder);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        await File.WriteAllTextAsync(Path.Combine(fourFolder, "project.db"), "not a database");
        try
        {
            var row = Assert.Single((await CastAsync(holmes.Series)).GetProperty("rows").EnumerateArray());
            Assert.Equal(watson, row.GetProperty("seriesCharacterId").GetGuid());

            await using var db = await SeriesDbContext.OpenAsync(Path.Combine(app.WorkspaceDir, ".series", holmes.Series.ToString("D")));
            Assert.Equal(2, await db.Links.CountAsync());
        }
        finally
        {
            // Other tests in this class heal only while every project reads.
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(fourFolder, recursive: true);
        }
    }

    [Fact]
    public async Task A_leaving_book_takes_its_links_and_a_Series_character_left_with_none_goes()
    {
        var holmes = await SeedAsync();
        var watson = await SharedAsync(holmes.Series, holmes.Scarlet.Folder, holmes.Scarlet.Watson);
        Assert.Equal(HttpStatusCode.NoContent, (await LinkAsync(holmes.Series, watson, holmes.Four.Folder, holmes.Four.Watson)).StatusCode);
        await SharedAsync(holmes.Series, holmes.Four.Folder, holmes.Four.Narrator);

        Assert.Equal(HttpStatusCode.NoContent, (await Http.DeleteAsync($"{app.BaseUrl}/api/series/{holmes.Series}/books/{holmes.Four.Folder}")).StatusCode);

        var row = Assert.Single((await CastAsync(holmes.Series)).GetProperty("rows").EnumerateArray());
        Assert.Equal(watson, row.GetProperty("seriesCharacterId").GetGuid());
        Assert.Equal(1, await StoredSeriesCharacterCountAsync(holmes.Series));
        Assert.Equal(JsonValueKind.Null, (await SummaryRowAsync(holmes.Four.Folder, holmes.Four.Watson)).GetProperty("seriesLink").ValueKind);

        Assert.Equal(HttpStatusCode.NoContent, (await Http.DeleteAsync($"{app.BaseUrl}/api/series/{holmes.Series}/books/{holmes.Scarlet.Folder}")).StatusCode);
        Assert.Equal(0, await StoredSeriesCharacterCountAsync(holmes.Series));
    }
}
