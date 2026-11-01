using System.Net;
using System.Text;
using System.Text.Json;
using Read2Me.E2eTests.Infrastructure;

namespace Read2Me.E2eTests.Tests.Api;

/// <summary>
/// The generic commands endpoint: one POST drives any BookCommand by discriminator. What each
/// outcome answers (a quiet null for a missing node, 422 for a narrator refusal) is
/// BookCommandApiContractTests; the narrator link on the wire is ReaderApiTests.
/// </summary>
public class CommandApiTests(E2eAppFixture app) : IClassFixture<E2eAppFixture>
{
    private static readonly HttpClient Http = new();

    private async Task<HttpResponseMessage> PostCommandAsync(string folder, string json) =>
        await Http.PostAsync($"{app.BaseUrl}/api/projects/{folder}/commands",
            new StringContent(json, Encoding.UTF8, "application/json"));

    [Fact]
    public async Task CreateCharacter_returns_id_and_shows_in_characters()
    {
        var folder = $"api-cmd-{Guid.NewGuid():N}";
        await app.SeedProjectAsync(folder, "Cmd Book", "Author");

        var response = await PostCommandAsync(folder,
            """{ "type": "CreateCharacter", "name": "Ganondorf" }""");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(doc.RootElement.GetProperty("newEntityId").GetGuid() != Guid.Empty);

        var characters = JsonDocument.Parse(
            await Http.GetStringAsync($"{app.BaseUrl}/api/projects/{folder}/characters"));
        Assert.Contains("Ganondorf", characters.RootElement.EnumerateArray()
            .Select(c => c.GetProperty("name").GetString()));
    }

    [Fact]
    public async Task SetParagraphCharacter_attributes_the_paragraph()
    {
        var folder = $"api-cmd2-{Guid.NewGuid():N}";
        var builder = await app.SeedProjectAsync(folder, "Cmd Book 2", "Author", characterName: "Alice");
        var paragraphId = builder.ParagraphId("p2");
        var characters = JsonDocument.Parse(
            await Http.GetStringAsync($"{app.BaseUrl}/api/projects/{folder}/characters"));
        var characterId = characters.RootElement.EnumerateArray()
            .Single(c => c.GetProperty("name").GetString() == "Alice")
            .GetProperty("id").GetGuid();

        var response = await PostCommandAsync(folder,
            $$"""{ "type": "SetParagraphCharacter", "paragraphId": "{{paragraphId}}", "characterId": "{{characterId}}" }""");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Unknown_command_type_is_400()
    {
        var folder = $"api-cmd3-{Guid.NewGuid():N}";
        await app.SeedProjectAsync(folder, "Cmd Book 3", "Author");

        var response = await PostCommandAsync(folder, """{ "type": "Explode" }""");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Unknown_folder_is_404()
    {
        var response = await PostCommandAsync("nope-cmd-xyz",
            """{ "type": "CreateCharacter", "name": "X" }""");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// The repair ADR 0005 deferred, driven straight through the API: the two-speaker item keeps its
    /// text and a new sibling arrives beside it, unattributed, so the attribution queue still owns
    /// the question of who speaks it. Whitespace is refused here and not only in the dialog — this
    /// endpoint is the agent path, and has no dialog in front of it.
    /// </summary>
    [Fact]
    public async Task InsertParagraphItem_adds_an_unattributed_sibling_and_refuses_blank_text()
    {
        var folder = $"api-insert-{Guid.NewGuid():N}";
        var builder = await app.SeedMisSplitParagraphProjectAsync(
            folder, "Insert Item Book", "Author", characterName: "Alice");
        var chapterId = builder.ChapterId("ch1");
        var paragraphId = builder.ParagraphId("p2");

        var response = await PostCommandAsync(folder,
            $$"""{ "type": "InsertParagraphItem", "anchorItemId": "{{builder.ItemId("mixed")}}", "position": "After", "text": "  “And who might you be?” he answered.  " }""");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var newId = JsonDocument.Parse(await response.Content.ReadAsStringAsync())
            .RootElement.GetProperty("newEntityId").GetGuid();
        Assert.NotEqual(Guid.Empty, newId);

        var items = await ReadItemsAsync(folder, chapterId, paragraphId);
        Assert.Equal(
            [builder.ItemId("lead"), builder.ItemId("mixed"), newId, builder.ItemId("clean")],
            items.Select(i => i.GetProperty("id").GetGuid()));

        var inserted = items.Single(i => i.GetProperty("id").GetGuid() == newId);
        Assert.Equal("“And who might you be?” he answered.", inserted.GetProperty("text").GetString());
        Assert.Equal(JsonValueKind.Null, inserted.GetProperty("characterId").ValueKind);

        var blank = await PostCommandAsync(folder,
            $$"""{ "type": "InsertParagraphItem", "anchorItemId": "{{builder.ItemId("mixed")}}", "position": "After", "text": "   " }""");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, blank.StatusCode);
        Assert.Equal(4, (await ReadItemsAsync(folder, chapterId, paragraphId)).Count);
    }

    private async Task<List<JsonElement>> ReadItemsAsync(string folder, Guid chapterId, Guid paragraphId)
    {
        var children = JsonDocument.Parse(await Http.GetStringAsync(
            $"{app.BaseUrl}/api/projects/{folder}/nodes/Chapter/{chapterId}/children"));
        return [.. children.RootElement.GetProperty("paragraphs").EnumerateArray()
            .Single(p => p.GetProperty("id").GetGuid() == paragraphId)
            .GetProperty("items").EnumerateArray().Select(i => i.Clone())];
    }
}
