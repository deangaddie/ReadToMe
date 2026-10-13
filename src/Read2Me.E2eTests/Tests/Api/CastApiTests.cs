using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Read2Me.E2eTests.Infrastructure;

namespace Read2Me.E2eTests.Tests.Api;

/// <summary>
/// The reads behind the web cast page (Angular ticket 15): the roster summary, a character's
/// lines and a line's surrounding paragraphs, and character discovery that fills the roster.
/// </summary>
public class CastApiTests(E2eAppFixture app) : IClassFixture<E2eAppFixture>
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
        Assert.Equal(HttpStatusCode.NotFound,
            (await Http.PostAsync($"{app.BaseUrl}/api/projects/nope-cast/characters/discover", null)).StatusCode);
    }

    /// <summary>Character discovery: one LLM call returns the cast, apply persists it.</summary>
    [Fact]
    public async Task Discover_returns_cast_and_apply_persists_it()
    {
        var folder = $"api-disc-{Guid.NewGuid():N}";
        await app.SeedProjectAsync(folder, "Disc Book", "Author");
        app.FakeAi.LlmReply = _ =>
            """{ "reasoning": "outline scan", "characters": [ { "name": "Link", "aliases": ["Hero of Time"] } ] }""";
        try
        {
            var discover = await Http.PostAsync(
                $"{app.BaseUrl}/api/projects/{folder}/characters/discover", null);
            Assert.Equal(HttpStatusCode.OK, discover.StatusCode);
            var outcome = JsonDocument.Parse(await discover.Content.ReadAsStringAsync());
            Assert.Equal("Ok", outcome.RootElement.GetProperty("status").GetString());
            var characters = outcome.RootElement.GetProperty("characters");
            Assert.Equal("Link", characters[0].GetProperty("name").GetString());
            // A fresh roster: nothing exists yet and nothing collides.
            Assert.Equal(JsonValueKind.Null, characters[0].GetProperty("existingCharacterId").ValueKind);
            Assert.Equal(0, outcome.RootElement.GetProperty("collisions").GetArrayLength());

            var apply = await Http.PostAsJsonAsync(
                $"{app.BaseUrl}/api/projects/{folder}/characters/discover/apply",
                new[] { new { name = "Link", aliases = new[] { "Hero of Time" } } });
            Assert.Equal(HttpStatusCode.OK, apply.StatusCode);
            var applied = JsonDocument.Parse(await apply.Content.ReadAsStringAsync());
            Assert.Equal(1, applied.RootElement.GetProperty("applied").GetInt32());

            var list = await GetJsonAsync($"/api/projects/{folder}/characters");
            var link = list.EnumerateArray().Single(c => c.GetProperty("name").GetString() == "Link");
            Assert.Contains("Hero of Time", link.GetProperty("aliases").EnumerateArray()
                .Select(a => a.GetProperty("name").GetString()));

            // Re-apply is idempotent: same character, no duplicate.
            var reapply = await Http.PostAsJsonAsync(
                $"{app.BaseUrl}/api/projects/{folder}/characters/discover/apply",
                new[] { new { name = "Link", aliases = new[] { "Hero of Time" } } });
            Assert.Equal(HttpStatusCode.OK, reapply.StatusCode);
            Assert.Single((await GetJsonAsync($"/api/projects/{folder}/characters")).EnumerateArray(),
                c => c.GetProperty("name").GetString() == "Link");

            // A second discovery resolves a row onto the roster character by its name ("Already
            // exists") — the way apply resolves it. A row that merely borrows a roster alias is not
            // that character: applying it would make a second owner, so it is reported as a collision,
            // as is an alias two new rows would both own.
            app.FakeAi.LlmReply = _ =>
                """{ "reasoning": "again", "characters": [ { "name": "link", "aliases": ["Hero of Time"] }, { "name": "Zelda", "aliases": ["Princess"] }, { "name": "Sheik", "aliases": ["Princess", "Link"] } ] }""";
            var again = JsonDocument.Parse(await (await Http.PostAsync(
                $"{app.BaseUrl}/api/projects/{folder}/characters/discover", null)).Content.ReadAsStringAsync());
            var rows = again.RootElement.GetProperty("characters").EnumerateArray().ToList();
            Assert.Equal(link.GetProperty("id").GetGuid(), rows[0].GetProperty("existingCharacterId").GetGuid());
            Assert.Equal(JsonValueKind.Null, rows[1].GetProperty("existingCharacterId").ValueKind);
            Assert.Equal(JsonValueKind.Null, rows[2].GetProperty("existingCharacterId").ValueKind);
            Assert.Equal(["Link", "Princess"],
                again.RootElement.GetProperty("collisions").EnumerateArray().Select(c => c.GetString()!).ToArray(),
                StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            app.FakeAi.Reset();
        }
    }
}
