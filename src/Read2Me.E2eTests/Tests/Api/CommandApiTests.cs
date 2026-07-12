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
}
