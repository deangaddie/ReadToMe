using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Read2Me.E2eTests.Infrastructure;

namespace Read2Me.E2eTests.Tests.Api;

/// <summary>
/// The reads behind the web cast page (Angular ticket 15): the roster summary, a character's
/// lines and a line's surrounding paragraphs.
/// </summary>
[Collection(E2eCollection.Name)]
public class CastApiTests(E2eAppFixture app)
{
    private static readonly HttpClient Http = new();

    private async Task<JsonElement> GetJsonAsync(string path)
    {
        var response = await Http.GetAsync($"{app.BaseUrl}{path}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private async Task RunAsync(string folder, object command)
    {
        var response = await Http.PostAsJsonAsync($"{app.BaseUrl}/api/projects/{folder}/commands", command);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Summary_lists_narrator_first_with_lines_aliases_voices_and_the_narrator_link()
    {
        var folder = $"api-cast-sum-{Guid.NewGuid():N}";
        var book = await app.SeedThreeDialogParagraphProjectAsync(folder, "Cast Book", "Author");
        var alice = book.CharacterId("Alice");
        await RunAsync(folder, new { type = "SetParagraphCharacter", paragraphId = book.ParagraphId("p2"), characterId = alice });
        await RunAsync(folder, new { type = "SetParagraphCharacter", paragraphId = book.ParagraphId("p3"), characterId = alice });
        await RunAsync(folder, new { type = "AddCharacterAlias", characterId = alice, name = "Al" });
        await app.SeedEditableVoiceAsync(folder, alice);

        var rows = (await GetJsonAsync($"/api/projects/{folder}/characters/summary")).EnumerateArray().ToList();

        Assert.Equal(["Narrator", "Alice"], rows.Select(r => r.GetProperty("name").GetString()!).ToArray());
        var narrator = rows[0];
        Assert.True(narrator.GetProperty("isNarrator").GetBoolean());
        Assert.False(narrator.GetProperty("narratesBook").GetBoolean());
        Assert.Equal(1, narrator.GetProperty("lineCount").GetInt32());

        var row = rows[1];
        Assert.Equal(alice, row.GetProperty("id").GetGuid());
        Assert.False(row.GetProperty("isNarrator").GetBoolean());
        Assert.Equal(2, row.GetProperty("lineCount").GetInt32());
        Assert.Equal(1, row.GetProperty("voiceCount").GetInt32());
        Assert.Equal(1, row.GetProperty("readyVoiceCount").GetInt32());
        var alias = Assert.Single(row.GetProperty("aliases").EnumerateArray());
        Assert.Equal("Al", alias.GetProperty("name").GetString());
        Assert.NotEqual(Guid.Empty, alias.GetProperty("id").GetGuid());

        // Linking the narrator flags the linked character, never the seed row.
        await RunAsync(folder, new { type = "SetNarratorCharacter", characterId = alice });
        var linked = (await GetJsonAsync($"/api/projects/{folder}/characters/summary")).EnumerateArray().ToList();
        Assert.False(linked[0].GetProperty("narratesBook").GetBoolean());
        Assert.True(linked[1].GetProperty("narratesBook").GetBoolean());
    }

    [Fact]
    public async Task Lines_are_the_items_a_character_speaks_in_book_order()
    {
        var folder = $"api-cast-lines-{Guid.NewGuid():N}";
        var book = await app.SeedThreeDialogParagraphProjectAsync(folder, "Lines Book", "Author");
        var alice = book.CharacterId("Alice");
        await RunAsync(folder, new { type = "SetParagraphCharacter", paragraphId = book.ParagraphId("p4"), characterId = alice });
        await RunAsync(folder, new { type = "SetParagraphCharacter", paragraphId = book.ParagraphId("p2"), characterId = alice });

        var lines = (await GetJsonAsync($"/api/projects/{folder}/characters/{alice}/lines")).EnumerateArray().ToList();

        Assert.Equal(2, lines.Count);
        Assert.Equal(book.ItemId("line1"), lines[0].GetProperty("itemId").GetGuid());
        Assert.Equal(book.ParagraphId("p2"), lines[0].GetProperty("paragraphId").GetGuid());
        Assert.Equal(book.ChapterId("ch1"), lines[0].GetProperty("chapterId").GetGuid());
        Assert.Equal("“Hello there,” she said.", lines[0].GetProperty("text").GetString());
        Assert.Equal(book.ItemId("line3"), lines[1].GetProperty("itemId").GetGuid());

        var none = await GetJsonAsync($"/api/projects/{folder}/characters/{Guid.NewGuid()}/lines");
        Assert.Equal(0, none.GetArrayLength());
    }

    [Fact]
    public async Task Context_returns_neighbours_in_the_chapter_with_speaker_names()
    {
        var folder = $"api-cast-ctx-{Guid.NewGuid():N}";
        var book = await app.SeedThreeDialogParagraphProjectAsync(folder, "Context Book", "Author");
        var alice = book.CharacterId("Alice");
        var chapter = book.ChapterId("ch1");
        await RunAsync(folder, new { type = "SetParagraphCharacter", paragraphId = book.ParagraphId("p2"), characterId = alice });

        var context = await GetJsonAsync(
            $"/api/projects/{folder}/paragraphs/{book.ParagraphId("p3")}/context?chapterId={chapter}&before=1&after=1");

        var before = Assert.Single(context.GetProperty("before").EnumerateArray());
        var beforeItem = Assert.Single(before.GetProperty("items").EnumerateArray());
        Assert.True(beforeItem.GetProperty("isDialog").GetBoolean());
        Assert.Equal("Alice", beforeItem.GetProperty("speaker").GetString());

        var query = context.GetProperty("paragraph");
        Assert.Equal("“Who goes there?” came the reply.", query.GetProperty("text").GetString());
        var queryItem = Assert.Single(query.GetProperty("items").EnumerateArray());
        Assert.True(queryItem.GetProperty("isDialog").GetBoolean());
        Assert.Equal(JsonValueKind.Null, queryItem.GetProperty("speaker").ValueKind);

        var after = Assert.Single(context.GetProperty("after").EnumerateArray());
        Assert.Equal(book.ItemId("line3"), Assert.Single(after.GetProperty("items").EnumerateArray()).GetProperty("itemId").GetGuid());

        // Defaults (3 before, 2 after) reach the narration paragraph; narration is not dialog.
        var wide = await GetJsonAsync($"/api/projects/{folder}/paragraphs/{book.ParagraphId("p3")}/context?chapterId={chapter}");
        var narration = wide.GetProperty("before").EnumerateArray().First();
        var narrationItem = Assert.Single(narration.GetProperty("items").EnumerateArray());
        Assert.False(narrationItem.GetProperty("isDialog").GetBoolean());

        var wrongChapter = await Http.GetAsync(
            $"{app.BaseUrl}/api/projects/{folder}/paragraphs/{book.ParagraphId("p3")}/context?chapterId={Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, wrongChapter.StatusCode);
    }

    [Fact]
    public async Task Unknown_folder_is_404_on_every_read()
    {
        Assert.Equal(HttpStatusCode.NotFound,
            (await Http.GetAsync($"{app.BaseUrl}/api/projects/nope-cast/characters/summary")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await Http.GetAsync($"{app.BaseUrl}/api/projects/nope-cast/characters/{Guid.NewGuid()}/lines")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await Http.GetAsync($"{app.BaseUrl}/api/projects/nope-cast/paragraphs/{Guid.NewGuid()}/context?chapterId={Guid.NewGuid()}")).StatusCode);
    }
}
