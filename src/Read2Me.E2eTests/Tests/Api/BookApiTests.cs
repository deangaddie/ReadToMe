using System.Net;
using System.Text;
using System.Text.Json;
using Read2Me.E2eTests.Infrastructure;

namespace Read2Me.E2eTests.Tests.Api;

/// <summary>
/// Book structure reads: overview, node children walk, characters with aliases, and the project
/// and node status roll-ups.
/// </summary>
public class BookApiTests(E2eAppFixture app) : IClassFixture<E2eAppFixture>
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

        var parts = await GetJsonAsync($"/api/projects/{folder}/nodes/Volume/{volumeId}/children");
        var partId = parts.RootElement.GetProperty("parts")[0].GetProperty("id").GetString();

        var chapters = await GetJsonAsync($"/api/projects/{folder}/nodes/Part/{partId}/children");
        var chapterId = chapters.RootElement.GetProperty("chapters")[0].GetProperty("id").GetString();

        var paragraphs = await GetJsonAsync($"/api/projects/{folder}/nodes/Chapter/{chapterId}/children");
        var paragraphArray = paragraphs.RootElement.GetProperty("paragraphs");
        Assert.Equal(3, paragraphArray.GetArrayLength());
        var firstItems = paragraphArray[0].GetProperty("items");
        Assert.True(firstItems.GetArrayLength() >= 1);
        Assert.False(string.IsNullOrEmpty(firstItems[0].GetProperty("text").GetString()));
    }

    /// <summary>
    /// The overview carries the Structure rules the node menu renders from: one entry per anchor
    /// kind, with enums by name. The rules themselves are BookHierarchyStructureRulesTests.
    /// </summary>
    [Fact]
    public async Task Overview_carries_the_structure_rules()
    {
        var folder = $"api-book-{Guid.NewGuid():N}";
        await app.SeedProjectAsync(folder, "Book Rules", "Author A");

        var rules = (await GetJsonAsync($"/api/projects/{folder}/book")).RootElement.GetProperty("structureRules");

        Assert.Equal(
            ["Volume", "Part", "Chapter", "Paragraph", "PauseParagraph", "ParagraphItem"],
            rules.EnumerateArray().Select(r => r.GetProperty("anchor").GetString()));
        var volume = rules[0];
        Assert.Equal("Volume", volume.GetProperty("nodeKind").GetString());
        Assert.Equal(1, volume.GetProperty("minimumCount").GetInt32());
        var atStart = volume.GetProperty("placements")[2];
        Assert.Equal("AtStart", atStart.GetProperty("placement").GetString());
        Assert.Equal("Part", atStart.GetProperty("creates").GetString());
        Assert.False(atStart.GetProperty("textRequired").GetBoolean());
        var pause = rules[4];
        Assert.Equal("Paragraph", pause.GetProperty("nodeKind").GetString());
        Assert.Equal(["Before", "After"],
            pause.GetProperty("placements").EnumerateArray().Select(p => p.GetProperty("placement").GetString()));
    }

    /// <summary>
    /// The roll-ups behind the web pipeline stepper and reader tree badges (ticket 09):
    /// <c>GET /api/projects/{folder}/status</c> and the per-node summary, read at the same revision
    /// as the overview. How a node rolls up is NodeStatusServiceTests; this is the endpoint's
    /// composition of it with the item and cast counts.
    /// </summary>
    [Fact]
    public async Task Status_rolls_up_items_and_volume_nodes_at_the_overview_revision()
    {
        var folder = $"api-book-{Guid.NewGuid():N}";
        await app.SeedProjectAsync(folder, "Book Status", "Author A");

        var overview = (await GetJsonAsync($"/api/projects/{folder}/book")).RootElement;
        var status = (await GetJsonAsync($"/api/projects/{folder}/status")).RootElement;

        Assert.Equal(status.GetProperty("revision").GetInt64(), overview.GetProperty("revision").GetInt64());
        Assert.True(status.GetProperty("hasContent").GetBoolean());
        var items = status.GetProperty("items");
        Assert.True(items.GetProperty("total").GetInt32() > 0);
        Assert.Equal(0, items.GetProperty("withAudio").GetInt32());
        Assert.True(items.GetProperty("unattributed").GetInt32() >= 1);

        var attributionRemaining = status.GetProperty("attribution").GetProperty("remaining").GetInt32();
        Assert.True(attributionRemaining >= 1);
        Assert.False(status.GetProperty("attribution").GetProperty("processing").GetBoolean());
        Assert.Equal(0, status.GetProperty("attribution").GetProperty("queued").GetInt32());
        Assert.True(status.GetProperty("audio").GetProperty("remaining").GetInt32() >= 1);
        // Narration is spoken by the seed narrator, so the book has at least one speaker.
        Assert.True(status.GetProperty("charactersWithLines").GetInt32() >= 1);

        var volumeId = Assert.Single(status.GetProperty("volumeIds").EnumerateArray()).GetString()!;
        var volume = status.GetProperty("nodes").GetProperty(volumeId);
        Assert.Equal(attributionRemaining, volume.GetProperty("attributionRemaining").GetInt32());
        Assert.Equal(status.GetProperty("audio").GetProperty("remaining").GetInt32(),
            volume.GetProperty("audioRemaining").GetInt32());

        var summary = (await GetJsonAsync($"/api/projects/{folder}/nodes/Volume/{volumeId}/status")).RootElement;
        Assert.Equal(attributionRemaining, summary.GetProperty("attributionRemaining").GetInt32());
        Assert.False(summary.GetProperty("isDone").GetBoolean());
    }

    [Fact]
    public async Task Status_of_a_project_with_no_book_has_no_content_and_zero_counts()
    {
        var form = new MultipartFormDataContent
        {
            { new StringContent($"api-status-fresh-{Guid.NewGuid():N}"), "title" },
            { new StringContent("A Book"), "bookTitle" },
            { new StringContent("An Author"), "author" },
        };
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes("Chapter 1\n\nIt was a dark and stormy night."));
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain");
        form.Add(file, "file", "book.txt");
        var create = await Http.PostAsync($"{app.BaseUrl}/api/projects", form);
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var folder = JsonDocument.Parse(await create.Content.ReadAsStringAsync())
            .RootElement.GetProperty("folderName").GetString()!;

        var status = (await GetJsonAsync($"/api/projects/{folder}/status")).RootElement;

        Assert.False(status.GetProperty("hasContent").GetBoolean());
        Assert.Equal(0, status.GetProperty("characters").GetInt32());
        Assert.Equal(0, status.GetProperty("charactersWithLines").GetInt32());
        Assert.Equal(0, status.GetProperty("readyVoices").GetInt32());
        Assert.Equal(0, status.GetProperty("items").GetProperty("total").GetInt32());
        Assert.Equal(0, status.GetProperty("attribution").GetProperty("remaining").GetInt32());
        Assert.Equal(0, status.GetProperty("audio").GetProperty("remaining").GetInt32());
        Assert.Equal(0, status.GetProperty("review").GetInt32());
        Assert.Empty(status.GetProperty("volumeIds").EnumerateArray());
        Assert.Empty(status.GetProperty("nodes").EnumerateObject());
        Assert.True(status.GetProperty("revision").GetInt64() >= 0);

        var badLevel = await Http.GetAsync($"{app.BaseUrl}/api/projects/{folder}/nodes/paragraph/{Guid.NewGuid()}/status");
        Assert.Equal(HttpStatusCode.BadRequest, badLevel.StatusCode);
        Assert.Equal("application/problem+json", badLevel.Content.Headers.ContentType?.MediaType);
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
    public async Task Unknown_folder_book_and_status_are_404()
    {
        var response = await Http.GetAsync($"{app.BaseUrl}/api/projects/nope-xyz/book");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var status = await Http.GetAsync($"{app.BaseUrl}/api/projects/nope-xyz/status");
        Assert.Equal(HttpStatusCode.NotFound, status.StatusCode);

        var node = await Http.GetAsync($"{app.BaseUrl}/api/projects/nope-xyz/nodes/Chapter/{Guid.NewGuid()}/status");
        Assert.Equal(HttpStatusCode.NotFound, node.StatusCode);
    }
}
