using System.Net;
using System.Text;
using System.Text.Json;
using Read2Me.E2eTests.Infrastructure;

namespace Read2Me.E2eTests.Tests.Api;

/// <summary>
/// The generic commands endpoint: one POST drives any BookCommand by discriminator.
/// </summary>
[Collection(E2eCollection.Name)]
public class CommandApiTests(E2eAppFixture app)
{
    private static readonly HttpClient Http = new();

    /// <summary>ProjectDbContext.NarratorId — the seed row an unlinked project reports.</summary>
    private static readonly Guid SeedNarratorId = new("00000000-0000-0000-0000-000000000001");

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
    public async Task SetNarratorCharacter_links_unlinks_and_rejects()
    {
        var folder = $"api-narr-{Guid.NewGuid():N}";
        await app.SeedProjectAsync(folder, "Narrator Book", "Author", characterName: "Watson");
        var characterId = JsonDocument.Parse(
                await Http.GetStringAsync($"{app.BaseUrl}/api/projects/{folder}/characters"))
            .RootElement.EnumerateArray()
            .Single(c => c.GetProperty("name").GetString() == "Watson")
            .GetProperty("id").GetGuid();

        // Unlinked reads as NarratorIdentity.Unlinked — never null.
        var before = await NarratorAsync(folder);
        Assert.False(before.GetProperty("isLinked").GetBoolean());
        Assert.Equal("Narrator", before.GetProperty("displayName").GetString());
        Assert.Equal(SeedNarratorId, before.GetProperty("characterId").GetGuid());

        var link = await PostCommandAsync(folder,
            $$"""{ "type": "SetNarratorCharacter", "characterId": "{{characterId}}" }""");
        Assert.Equal(HttpStatusCode.OK, link.StatusCode);

        var linked = await NarratorAsync(folder);
        Assert.True(linked.GetProperty("isLinked").GetBoolean());
        Assert.Equal(characterId, linked.GetProperty("characterId").GetGuid());
        Assert.Equal("Watson", linked.GetProperty("displayName").GetString());

        // A bad target must surface as 422, not as 200 with a null id.
        var rejected = await PostCommandAsync(folder,
            $$"""{ "type": "SetNarratorCharacter", "characterId": "{{Guid.NewGuid()}}" }""");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, rejected.StatusCode);
        Assert.True((await NarratorAsync(folder)).GetProperty("isLinked").GetBoolean());

        var unlink = await PostCommandAsync(folder,
            """{ "type": "SetNarratorCharacter", "characterId": null }""");
        Assert.Equal(HttpStatusCode.OK, unlink.StatusCode);
        Assert.False((await NarratorAsync(folder)).GetProperty("isLinked").GetBoolean());
    }

    private async Task<JsonElement> NarratorAsync(string folder) =>
        JsonDocument.Parse(await Http.GetStringAsync($"{app.BaseUrl}/api/projects/{folder}"))
            .RootElement.GetProperty("narrator").Clone();

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
}
