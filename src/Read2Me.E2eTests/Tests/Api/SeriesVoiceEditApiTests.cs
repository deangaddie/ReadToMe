using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Read2Me.E2eTests.Infrastructure;

namespace Read2Me.E2eTests.Tests.Api;

/// <summary>
/// Shared Voice edits (#221): an edit of a series-linked Character's Voice through one book's
/// per-book routes is copied into every other linked book in the same request, each copy a History
/// step there with the same label; the delete and merge refusals; a Restore stays local; and the
/// inline editor's read <c>GET /api/series/{id}/characters/{scId}</c>. The books are the sample series (#209).
/// </summary>
public class SeriesVoiceEditApiTests(E2eAppFixture app) : IClassFixture<E2eAppFixture>
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

    // ---- copies ----------------------------------------------------------------------------------

    [Fact]
    public async Task A_Voice_made_and_renamed_in_one_book_is_in_every_linked_book_each_with_a_History_step_of_the_same_label()
    {
        var holmes = await SeedAsync();

        var voice = await CreateVoiceAsync(holmes.Four, "Army doctor");

        foreach (var book in holmes.All)
        {
            Assert.Equal("Army doctor", (await VoiceAsync(book, voice))?.GetProperty("name").GetString());
            Assert.Equal(voice, await DefaultAsync(book)); // the first Voice is each book's default
            Assert.Contains("New voice", await HistoryLabelsAsync(book.Folder));
        }

        await RunAsync(holmes.Adventures.Folder, new { type = "UpdateVoice", voiceId = voice, name = "Old campaigner", description = "gruff" });

        foreach (var book in holmes.All)
        {
            var copy = (await VoiceAsync(book, voice))!.Value;
            Assert.Equal("Old campaigner", copy.GetProperty("name").GetString());
            Assert.Equal("gruff", copy.GetProperty("description").GetString());
            Assert.Equal("Edit voice", (await HistoryLabelsAsync(book.Folder))[0]);
        }
    }

    [Fact]
    public async Task Generated_audio_is_copied_into_each_book_under_its_own_Character()
    {
        var holmes = await SeedAsync();
        var voice = await CreateVoiceAsync(holmes.Scarlet, "Army doctor", generated: true);
        await RunAsync(holmes.Scarlet.Folder, new { type = "SetVoiceDesignPrompt", voiceId = voice, prompt = "A warm army doctor." });

        var generated = await Http.PostAsync(
            $"{app.BaseUrl}/api/projects/{holmes.Scarlet.Folder}/characters/{holmes.Scarlet.Watson}/voices/{voice}/generate-audio", null);
        Assert.Equal(HttpStatusCode.OK, generated.StatusCode);

        foreach (var book in new[] { holmes.Four, holmes.Adventures })
        {
            var copy = (await VoiceAsync(book, voice))!.Value;
            Assert.Equal("A warm army doctor.", copy.GetProperty("designPrompt").GetString());
            var audio = copy.GetProperty("audioFileName").GetString()!;
            Assert.StartsWith($"voices/{book.Watson}/", audio);
            Assert.True(File.Exists(Path.Combine(app.WorkspaceDir, book.Folder, audio.Replace('/', Path.DirectorySeparatorChar))));
            Assert.Equal("Generate voice audio", (await HistoryLabelsAsync(book.Folder))[0]);
        }
    }

    [Fact]
    public async Task A_Restore_stays_in_its_own_book()
    {
        var holmes = await SeedAsync();
        var voice = await CreateVoiceAsync(holmes.Scarlet, "Army doctor");
        var before = (await GetJsonAsync($"/api/projects/{holmes.Scarlet.Folder}/history")).GetProperty("headStepId").GetGuid();
        await RunAsync(holmes.Scarlet.Folder, new { type = "UpdateVoice", voiceId = voice, name = "Old campaigner" });

        var restore = await Http.PostAsync($"{app.BaseUrl}/api/projects/{holmes.Scarlet.Folder}/history/{before}/restore", null);

        Assert.Equal(HttpStatusCode.OK, restore.StatusCode);
        Assert.Equal("Army doctor", (await VoiceAsync(holmes.Scarlet, voice))?.GetProperty("name").GetString());
        Assert.Equal("Old campaigner", (await VoiceAsync(holmes.Four, voice))?.GetProperty("name").GetString());
        Assert.Equal("Old campaigner", (await VoiceAsync(holmes.Adventures, voice))?.GetProperty("name").GetString());
    }

    // ---- delete and merge ------------------------------------------------------------------------

    [Fact]
    public async Task Deleting_a_shared_Voice_is_refused_while_another_books_rules_use_it_or_it_is_the_last()
    {
        var holmes = await SeedAsync();
        var first = await CreateVoiceAsync(holmes.Scarlet, "Army doctor");

        var last = await CommandAsync(holmes.Scarlet.Folder, new { type = "DeleteVoice", voiceId = first });
        Assert.Equal(HttpStatusCode.Conflict, last.StatusCode);
        var lastBody = await JsonAsync(last);
        Assert.Equal("LastVoice", lastBody.GetProperty("reason").GetString());
        Assert.Equal([holmes.Four.Folder, holmes.Adventures.Folder],
            lastBody.GetProperty("books").EnumerateArray().Select(b => b.GetProperty("folder").GetString()).ToArray());

        var second = await CreateVoiceAsync(holmes.Scarlet, "Old campaigner");
        await RunAsync(holmes.Four.Folder, new
        {
            type = "CreateVoiceRule", characterId = holmes.Four.Watson, voiceId = first, fromLevel = "Chapter", fromNodeId = holmes.Four.Chapter2,
        });

        var ruled = await CommandAsync(holmes.Adventures.Folder, new { type = "DeleteVoice", voiceId = first });
        Assert.Equal(HttpStatusCode.Conflict, ruled.StatusCode);
        var ruledBody = await JsonAsync(ruled);
        Assert.Equal("PositionalRules", ruledBody.GetProperty("reason").GetString());
        Assert.Contains("The Sign of the Four", ruledBody.GetProperty("detail").GetString());
        var blocker = Assert.Single(ruledBody.GetProperty("books").EnumerateArray());
        Assert.Equal(holmes.Four.Folder, blocker.GetProperty("folder").GetString());
        Assert.Equal(1, blocker.GetProperty("ruleCount").GetInt32());
        foreach (var book in holmes.All)
            Assert.NotNull(await VoiceAsync(book, first));

        // The editor's read names the same blocks.
        var detail = await GetJsonAsync($"/api/series/{holmes.Series}/characters/{holmes.SeriesWatson}");
        Assert.Equal(holmes.Scarlet.Folder, detail.GetProperty("seriesCopyFolder").GetString());
        Assert.Equal(["Army doctor", "Old campaigner"],
            detail.GetProperty("voices").EnumerateArray().Select(v => v.GetProperty("name").GetString()).ToArray());
        Assert.Equal([holmes.Scarlet.Folder, holmes.Four.Folder, holmes.Adventures.Folder],
            detail.GetProperty("books").EnumerateArray().Select(b => b.GetProperty("folder").GetString()).ToArray());
        Assert.Equal("PositionalRules", detail.GetProperty("deleteBlocks").GetProperty(first.ToString()).GetProperty("reason").GetString());
        Assert.False(detail.GetProperty("deleteBlocks").TryGetProperty(second.ToString(), out _));

        // A Voice nothing else uses goes from every book; a book whose default it was moves to its oldest remaining Voice.
        await RunAsync(holmes.Four.Folder, new { type = "SetVoiceDefault", voiceId = second });
        Assert.Equal(HttpStatusCode.OK, (await CommandAsync(holmes.Scarlet.Folder, new { type = "DeleteVoice", voiceId = second })).StatusCode);
        foreach (var book in holmes.All)
        {
            Assert.Null(await VoiceAsync(book, second));
            Assert.Equal(first, await DefaultAsync(book));
            Assert.Equal("Delete voice", (await HistoryLabelsAsync(book.Folder))[0]);
        }
    }

    [Fact]
    public async Task A_linked_Character_cant_be_merged_away_and_deleting_it_unlinks_it_in_that_book_only()
    {
        var holmes = await SeedAsync();
        var voice = await CreateVoiceAsync(holmes.Scarlet, "Army doctor");
        var john = await RunAsync(holmes.Four.Folder, new { type = "CreateCharacter", name = "John" });

        var merged = await CommandAsync(holmes.Four.Folder, new { type = "MergeCharacters", survivorId = john, mergedId = holmes.Four.Watson, addNameAsAlias = true });
        Assert.Equal(HttpStatusCode.Conflict, merged.StatusCode);
        Assert.Contains("Unlink Watson first", (await JsonAsync(merged)).GetProperty("detail").GetString());

        Assert.Equal(HttpStatusCode.OK, (await CommandAsync(holmes.Four.Folder,
            new { type = "MergeCharacters", survivorId = holmes.Four.Watson, mergedId = john, addNameAsAlias = true })).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await CommandAsync(holmes.Adventures.Folder,
            new { type = "DeleteCharacter", characterId = holmes.Adventures.Watson })).StatusCode);

        var row = Assert.Single((await GetJsonAsync($"/api/series/{holmes.Series}/cast")).GetProperty("rows").EnumerateArray());
        var cells = row.GetProperty("cells").EnumerateArray().ToDictionary(c => c.GetProperty("folder").GetString()!);
        Assert.Equal(JsonValueKind.Null, cells[holmes.Adventures.Folder].GetProperty("linked").ValueKind);
        Assert.Equal(JsonValueKind.Object, cells[holmes.Four.Folder].GetProperty("linked").ValueKind);
        Assert.NotNull(await VoiceAsync(holmes.Scarlet, voice));
        Assert.NotNull(await VoiceAsync(holmes.Four, voice));
    }

    [Fact]
    public async Task The_editor_read_refuses_an_unknown_series_character()
    {
        var holmes = await SeedAsync();
        Assert.Equal(HttpStatusCode.NotFound,
            (await Http.GetAsync($"{app.BaseUrl}/api/series/{holmes.Series}/characters/{Guid.NewGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await Http.GetAsync($"{app.BaseUrl}/api/series/{Guid.NewGuid()}/characters/{holmes.SeriesWatson}")).StatusCode);
    }
}
