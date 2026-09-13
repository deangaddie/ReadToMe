using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Read2Me.E2eTests.Infrastructure;

namespace Read2Me.E2eTests.Tests.Api;

/// <summary>
/// The roll-ups behind the Angular pipeline stepper and reader tree badges (ticket 09):
/// <c>GET /api/projects/{folder}/status</c> and the per-node summary.
/// </summary>
[Collection(E2eCollection.Name)]
public class ProjectStatusApiTests(E2eAppFixture app)
{
    private static readonly HttpClient Http = new();

    private async Task<string> CreateProjectAsync(string prefix)
    {
        var form = new MultipartFormDataContent
        {
            { new StringContent($"{prefix}-{Guid.NewGuid():N}"), "title" },
            { new StringContent("A Book"), "bookTitle" },
            { new StringContent("An Author"), "author" },
        };
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(
            "Chapter 1\n\nIt was a dark and stormy night.\n\n“Hello,” she said.\n\nThe rain went on."));
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain");
        form.Add(file, "file", "book.txt");

        var create = await Http.PostAsync($"{app.BaseUrl}/api/projects", form);
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        return JsonDocument.Parse(await create.Content.ReadAsStringAsync())
            .RootElement.GetProperty("folderName").GetString()!;
    }

    private async Task<JsonElement> StatusAsync(string folder)
    {
        var response = await Http.GetAsync($"{app.BaseUrl}/api/projects/{folder}/status");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
    }

    [Fact]
    public async Task Fresh_project_has_no_content_and_zero_counts()
    {
        var folder = await CreateProjectAsync("api-status-fresh");

        var status = await StatusAsync(folder);

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
    }

    [Fact]
    public async Task Imported_project_rolls_up_items_and_volume_nodes()
    {
        var folder = await CreateProjectAsync("api-status-import");
        var import = await Http.PostAsJsonAsync($"{app.BaseUrl}/api/projects/{folder}/import", new { reread = false });
        Assert.Equal(HttpStatusCode.OK, import.StatusCode);

        var status = await StatusAsync(folder);

        Assert.True(status.GetProperty("hasContent").GetBoolean());
        var items = status.GetProperty("items");
        var total = items.GetProperty("total").GetInt32();
        Assert.True(total > 0);
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

        var node = await Http.GetAsync($"{app.BaseUrl}/api/projects/{folder}/nodes/volume/{volumeId}/status");
        Assert.Equal(HttpStatusCode.OK, node.StatusCode);
        var summary = JsonDocument.Parse(await node.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(attributionRemaining, summary.GetProperty("attributionRemaining").GetInt32());
        Assert.False(summary.GetProperty("isDone").GetBoolean());
    }

    [Fact]
    public async Task Unknown_project_is_404_and_bad_level_is_400()
    {
        var missing = await Http.GetAsync($"{app.BaseUrl}/api/projects/no-such-project-xyz/status");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        var missingNode = await Http.GetAsync(
            $"{app.BaseUrl}/api/projects/no-such-project-xyz/nodes/chapter/{Guid.NewGuid()}/status");
        Assert.Equal(HttpStatusCode.NotFound, missingNode.StatusCode);

        var folder = await CreateProjectAsync("api-status-level");
        var badLevel = await Http.GetAsync($"{app.BaseUrl}/api/projects/{folder}/nodes/paragraph/{Guid.NewGuid()}/status");
        Assert.Equal(HttpStatusCode.BadRequest, badLevel.StatusCode);
        Assert.Equal("application/problem+json", badLevel.Content.Headers.ContentType?.MediaType);
    }
}
