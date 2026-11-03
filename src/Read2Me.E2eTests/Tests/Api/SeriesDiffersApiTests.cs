using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Read2Me.E2eTests.Infrastructure;

namespace Read2Me.E2eTests.Tests.Api;

/// <summary>
/// Differs from series (#222): a Restore to older Voices makes a book differ, as the board, the
/// editor read and <c>seriesLink</c> show; the series copy is the version most books hold; push and
/// take resolve it, each a History step, and refuse a book that doesn't differ; and an edit made in
/// the series copy brings a differing book back in line. The books are the sample series (#209).
/// </summary>
public class SeriesDiffersApiTests(E2eAppFixture app) : IClassFixture<E2eAppFixture>
{
    private static readonly HttpClient Http = new();

    private sealed record Book(string Folder, Guid Watson, Guid Chapter2);

    private sealed record Holmes(Guid Series, Guid SeriesWatson, Book Scarlet, Book Four, Book Adventures)
    {
        public IEnumerable<Book> All => [Scarlet, Four, Adventures];
    }

    /// <summary>Three Holmes books in one series, in that order, with Watson shared from Scarlet and linked in the other two.</summary>
    private async Task<Holmes> SeedAsync()
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        async Task<Book> BookAsync(string folder, string title)
        {
            var book = await app.SeedMultiChapterProjectAsync(folder, title, "Arthur Conan Doyle", chapters: 3, characterName: "Watson");
            return new Book(folder, book.CharacterId("Watson"), book.ChapterId("Chapter 2"));
        }

        var scarlet = await BookAsync($"scarlet-{tag}", "A Study in Scarlet");
        var four = await BookAsync($"four-{tag}", "The Sign of the Four");
        var adventures = await BookAsync($"adventures-{tag}", "The Adventures of Sherlock Holmes");

        var created = await Http.PostAsJsonAsync($"{app.BaseUrl}/api/series", new { name = $"Sherlock Holmes {tag}", folder = scarlet.Folder });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var series = (await JsonAsync(created)).GetProperty("id").GetGuid();
        foreach (var book in new[] { four, adventures })
            Assert.Equal(HttpStatusCode.OK, (await Http.PutAsJsonAsync($"{app.BaseUrl}/api/series/{series}/books/{book.Folder}", new { })).StatusCode);

        var shared = await Http.PostAsJsonAsync($"{app.BaseUrl}/api/series/{series}/characters", new { folder = scarlet.Folder, characterId = scarlet.Watson });
        Assert.Equal(HttpStatusCode.Created, shared.StatusCode);
        var watson = (await JsonAsync(shared)).GetProperty("seriesCharacterId").GetGuid();
        foreach (var book in new[] { four, adventures })
            Assert.Equal(HttpStatusCode.NoContent, (await Http.PutAsJsonAsync(
                $"{app.BaseUrl}/api/series/{series}/characters/{watson}/links/{book.Folder}", new { characterId = book.Watson })).StatusCode);
        return new Holmes(series, watson, scarlet, four, adventures);
    }

    private Task<HttpResponseMessage> CommandAsync(string folder, object command) =>
        Http.PostAsJsonAsync($"{app.BaseUrl}/api/projects/{folder}/commands", command);

    private async Task<Guid> RunAsync(string folder, object command)
    {
        var response = await CommandAsync(folder, command);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await JsonAsync(response);
        return body.TryGetProperty("newEntityId", out var id) && id.ValueKind == JsonValueKind.String ? id.GetGuid() : Guid.Empty;
    }

    private Task<Guid> CreateVoiceAsync(Book book, string name, bool generated = false) =>
        RunAsync(book.Folder, new { type = "CreateVoice", characterId = book.Watson, name, isGenerated = generated });

    private async Task<JsonElement> VoicesAsync(Book book) =>
        await GetJsonAsync($"/api/projects/{book.Folder}/characters/{book.Watson}/voices");

    private async Task<JsonElement?> VoiceAsync(Book book, Guid voiceId) =>
        (await VoicesAsync(book)).GetProperty("voices").EnumerateArray()
            .Select(v => (JsonElement?)v).FirstOrDefault(v => v!.Value.GetProperty("id").GetGuid() == voiceId);

    private async Task<Guid?> DefaultAsync(Book book) =>
        (await VoicesAsync(book)).GetProperty("defaultVoiceId") is { ValueKind: JsonValueKind.String } d ? d.GetGuid() : null;

    private async Task<IReadOnlyList<string>> HistoryLabelsAsync(string folder) =>
        (await GetJsonAsync($"/api/projects/{folder}/history")).GetProperty("steps").EnumerateArray()
            .Select(s => s.GetProperty("label").GetString()!).ToList();

    private async Task<JsonElement> GetJsonAsync(string path)
    {
        var response = await Http.GetAsync($"{app.BaseUrl}{path}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await JsonAsync(response);
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();


    private Task<HttpResponseMessage> ResolveAsync(Holmes holmes, string how, Book book) =>
        Http.PostAsJsonAsync($"{app.BaseUrl}/api/series/{holmes.Series}/characters/{holmes.SeriesWatson}/{how}", new { folder = book.Folder });

    private async Task<string?> NameAsync(Book book, Guid voiceId) =>
        (await VoiceAsync(book, voiceId))?.GetProperty("name").GetString();

    private async Task<Dictionary<string, bool>> BoardDiffersAsync(Holmes holmes)
    {
        var row = Assert.Single((await GetJsonAsync($"/api/series/{holmes.Series}/cast")).GetProperty("rows").EnumerateArray());
        return row.GetProperty("cells").EnumerateArray().ToDictionary(
            c => c.GetProperty("folder").GetString()!, c => c.GetProperty("linked").GetProperty("differs").GetBoolean());
    }

    private async Task<JsonElement> DetailAsync(Holmes holmes) =>
        await GetJsonAsync($"/api/series/{holmes.Series}/characters/{holmes.SeriesWatson}");

    private async Task<bool> LinkDiffersAsync(Book book) =>
        (await GetJsonAsync($"/api/projects/{book.Folder}/characters/summary")).EnumerateArray()
            .Single(r => r.GetProperty("id").GetGuid() == book.Watson).GetProperty("seriesLink").GetProperty("differs").GetBoolean();

    private async Task<Guid> HeadAsync(Book book) =>
        (await GetJsonAsync($"/api/projects/{book.Folder}/history")).GetProperty("headStepId").GetGuid();

    private async Task RestoreAsync(Book book, Guid step) =>
        Assert.Equal(HttpStatusCode.OK, (await Http.PostAsync($"{app.BaseUrl}/api/projects/{book.Folder}/history/{step}/restore", null)).StatusCode);

    /// <summary>A shared Voice "Army doctor", renamed "Old campaigner" in every book, then Scarlet Restored to before the rename.</summary>
    private async Task<(Holmes Holmes, Guid Voice)> SeedRestoredAsync()
    {
        var holmes = await SeedAsync();
        var voice = await CreateVoiceAsync(holmes.Scarlet, "Army doctor");
        var before = await HeadAsync(holmes.Scarlet);
        await RunAsync(holmes.Four.Folder, new { type = "UpdateVoice", voiceId = voice, name = "Old campaigner" });
        await RestoreAsync(holmes.Scarlet, before);
        return (holmes, voice);
    }

    [Fact]
    public async Task Linked_books_holding_the_same_Voices_differ_in_none_and_push_and_take_refuse()
    {
        var holmes = await SeedAsync();
        await CreateVoiceAsync(holmes.Scarlet, "Army doctor");

        Assert.All((await BoardDiffersAsync(holmes)).Values, Assert.False);
        Assert.All((await DetailAsync(holmes)).GetProperty("books").EnumerateArray(), b => Assert.False(b.GetProperty("differs").GetBoolean()));
        Assert.False(await LinkDiffersAsync(holmes.Scarlet));
        Assert.Equal(HttpStatusCode.Conflict, (await ResolveAsync(holmes, "push", holmes.Scarlet)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await ResolveAsync(holmes, "take", holmes.Four)).StatusCode);
    }

    [Fact]
    public async Task A_Restore_makes_its_book_differ_and_the_series_copy_is_what_the_other_books_hold()
    {
        var (holmes, voice) = await SeedRestoredAsync();

        Assert.Equal(
            new Dictionary<string, bool> { [holmes.Scarlet.Folder] = true, [holmes.Four.Folder] = false, [holmes.Adventures.Folder] = false },
            await BoardDiffersAsync(holmes));
        Assert.True(await LinkDiffersAsync(holmes.Scarlet));
        Assert.False(await LinkDiffersAsync(holmes.Four));

        var detail = await DetailAsync(holmes);
        Assert.Equal(holmes.Four.Folder, detail.GetProperty("seriesCopyFolder").GetString());
        Assert.Equal("Old campaigner", Assert.Single(detail.GetProperty("voices").EnumerateArray()).GetProperty("name").GetString());
        Assert.Equal([true, false, false],
            detail.GetProperty("books").EnumerateArray().Select(b => b.GetProperty("differs").GetBoolean()).ToArray());
        Assert.Equal("Army doctor", await NameAsync(holmes.Scarlet, voice));
    }

    [Fact]
    public async Task Take_copies_the_series_copy_into_the_book_that_differs()
    {
        var (holmes, voice) = await SeedRestoredAsync();

        Assert.Equal(HttpStatusCode.Conflict, (await ResolveAsync(holmes, "take", holmes.Four)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await ResolveAsync(holmes, "take", holmes.Scarlet)).StatusCode);

        foreach (var book in holmes.All)
            Assert.Equal("Old campaigner", await NameAsync(book, voice));
        Assert.Equal("Take series voices", (await HistoryLabelsAsync(holmes.Scarlet.Folder))[0]);
        Assert.Equal("Edit voice", (await HistoryLabelsAsync(holmes.Four.Folder))[0]);
        Assert.All((await BoardDiffersAsync(holmes)).Values, Assert.False);
        Assert.Equal(HttpStatusCode.Conflict, (await ResolveAsync(holmes, "take", holmes.Scarlet)).StatusCode);
    }

    [Fact]
    public async Task Push_copies_the_differing_books_Voices_into_every_other_linked_book()
    {
        var (holmes, voice) = await SeedRestoredAsync();

        Assert.Equal(HttpStatusCode.Conflict, (await ResolveAsync(holmes, "push", holmes.Adventures)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await ResolveAsync(holmes, "push", holmes.Scarlet)).StatusCode);

        foreach (var book in holmes.All)
            Assert.Equal("Army doctor", await NameAsync(book, voice));
        Assert.Equal("Push series voices", (await HistoryLabelsAsync(holmes.Four.Folder))[0]);
        Assert.Equal("Push series voices", (await HistoryLabelsAsync(holmes.Adventures.Folder))[0]);
        Assert.NotEqual("Push series voices", (await HistoryLabelsAsync(holmes.Scarlet.Folder))[0]);
        Assert.All((await BoardDiffersAsync(holmes)).Values, Assert.False);
        Assert.Equal(HttpStatusCode.Conflict, (await ResolveAsync(holmes, "push", holmes.Scarlet)).StatusCode);
    }

    [Fact]
    public async Task Take_copies_only_Voices_and_the_book_keeps_its_own_default()
    {
        var holmes = await SeedAsync();
        var first = await CreateVoiceAsync(holmes.Scarlet, "Army doctor");
        var second = await CreateVoiceAsync(holmes.Scarlet, "Old campaigner");
        await RunAsync(holmes.Four.Folder, new { type = "SetVoiceDefault", voiceId = second });
        var before = await HeadAsync(holmes.Four);
        await RunAsync(holmes.Scarlet.Folder, new { type = "UpdateVoice", voiceId = first, name = "Retired" });
        await RestoreAsync(holmes.Four, before);

        Assert.Equal(HttpStatusCode.NoContent, (await ResolveAsync(holmes, "take", holmes.Four)).StatusCode);

        Assert.Equal("Retired", await NameAsync(holmes.Four, first));
        Assert.Equal(second, await DefaultAsync(holmes.Four));
        Assert.Equal(first, await DefaultAsync(holmes.Scarlet));
    }

    [Fact]
    public async Task Push_and_take_refuse_an_unknown_series_character_or_book_and_a_body_without_a_folder()
    {
        var holmes = await SeedAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await Http.PostAsJsonAsync(
            $"{app.BaseUrl}/api/series/{holmes.Series}/characters/{Guid.NewGuid()}/push", new { folder = holmes.Scarlet.Folder })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Http.PostAsJsonAsync(
            $"{app.BaseUrl}/api/series/{holmes.Series}/characters/{holmes.SeriesWatson}/take", new { folder = "no-such-book" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Http.PostAsJsonAsync(
            $"{app.BaseUrl}/api/series/{holmes.Series}/characters/{holmes.SeriesWatson}/take", new { })).StatusCode);
    }

    [Fact]
    public async Task An_edit_made_in_the_series_copy_brings_the_differing_book_back_in_line()
    {
        var (holmes, voice) = await SeedRestoredAsync();
        Assert.Equal(holmes.Four.Folder, (await DetailAsync(holmes)).GetProperty("seriesCopyFolder").GetString());

        var added = await CreateVoiceAsync(holmes.Four, "Retired");

        foreach (var book in holmes.All)
        {
            Assert.Equal("Old campaigner", await NameAsync(book, voice));
            Assert.Equal("Retired", await NameAsync(book, added));
            Assert.Equal("New voice", (await HistoryLabelsAsync(book.Folder))[0]);
        }
        Assert.All((await BoardDiffersAsync(holmes)).Values, Assert.False);
    }

    [Fact]
    public async Task A_book_that_lacks_a_Voice_doesnt_stop_another_books_last_Voice_going()
    {
        var holmes = await SeedAsync();
        var before = await HeadAsync(holmes.Four);
        var voice = await CreateVoiceAsync(holmes.Scarlet, "Army doctor");
        await RestoreAsync(holmes.Four, before); // Four never had it
        await UnlinkAsync(holmes, holmes.Adventures);

        Assert.Equal(HttpStatusCode.OK, (await CommandAsync(holmes.Scarlet.Folder, new { type = "DeleteVoice", voiceId = voice })).StatusCode);
        Assert.Null(await VoiceAsync(holmes.Scarlet, voice));
    }

    private async Task UnlinkAsync(Holmes holmes, Book book) =>
        Assert.Equal(HttpStatusCode.NoContent,
            (await Http.DeleteAsync($"{app.BaseUrl}/api/series/{holmes.Series}/characters/{holmes.SeriesWatson}/links/{book.Folder}")).StatusCode);
}
