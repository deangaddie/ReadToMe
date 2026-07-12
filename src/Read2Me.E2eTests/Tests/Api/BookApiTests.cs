using System.Net;
using System.Text.Json;
using Read2Me.E2eTests.Infrastructure;

namespace Read2Me.E2eTests.Tests.Api;

/// <summary>
/// Book structure reads: overview, node children walk, characters with aliases.
/// </summary>
[Collection(E2eCollection.Name)]
public class BookApiTests(E2eAppFixture app)
{
    private static readonly HttpClient Http = new();

    private async Task<JsonDocument> GetJsonAsync(string path)
    {
        var response = await Http.GetAsync($"{app.BaseUrl}{path}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Overview_children_walk_reaches_paragraph_items()
    {
        var folder = $"api-book-{Guid.NewGuid():N}";
        await app.SeedProjectAsync(folder, "Book Walk", "Author A");

        var overview = await GetJsonAsync($"/api/projects/{folder}/book");
        Assert.True(overview.RootElement.GetProperty("hasContent").GetBoolean());
        var volumes = overview.RootElement.GetProperty("volumes");
        Assert.Equal(1, volumes.GetArrayLength());
        var volumeId = volumes[0].GetProperty("id").GetString();

        var parts = await GetJsonAsync($"/api/projects/{folder}/nodes/volume/{volumeId}/children");
        var partId = parts.RootElement.GetProperty("parts")[0].GetProperty("id").GetString();

        var chapters = await GetJsonAsync($"/api/projects/{folder}/nodes/part/{partId}/children");
        var chapterId = chapters.RootElement.GetProperty("chapters")[0].GetProperty("id").GetString();

        var paragraphs = await GetJsonAsync($"/api/projects/{folder}/nodes/chapter/{chapterId}/children");
        var paragraphArray = paragraphs.RootElement.GetProperty("paragraphs");
        Assert.Equal(3, paragraphArray.GetArrayLength());
        var firstItems = paragraphArray[0].GetProperty("items");
        Assert.True(firstItems.GetArrayLength() >= 1);
        Assert.False(string.IsNullOrEmpty(firstItems[0].GetProperty("text").GetString()));
    }

    [Fact]
    public async Task Characters_include_seeded_character()
    {
        var folder = $"api-chars-{Guid.NewGuid():N}";
        await app.SeedProjectAsync(folder, "Char Book", "Author B", characterName: "Zelda");

        var doc = await GetJsonAsync($"/api/projects/{folder}/characters");

        var names = doc.RootElement.EnumerateArray()
            .Select(c => c.GetProperty("name").GetString()).ToList();
        Assert.Contains("Zelda", names);
    }

    [Fact]
    public async Task Unknown_level_is_400()
    {
        var folder = $"api-lvl-{Guid.NewGuid():N}";
        await app.SeedProjectAsync(folder, "Level Book", "Author C");

        var response = await Http.GetAsync(
            $"{app.BaseUrl}/api/projects/{folder}/nodes/paragraph/{Guid.NewGuid()}/children");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Unknown_folder_book_is_404()
    {
        var response = await Http.GetAsync($"{app.BaseUrl}/api/projects/nope-xyz/book");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
