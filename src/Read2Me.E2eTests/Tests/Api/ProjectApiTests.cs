using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Read2Me.E2eTests.Infrastructure;

namespace Read2Me.E2eTests.Tests.Api;

/// <summary>
/// Vertical slice of the agent-facing API: project list/create/detail/delete,
/// import, and the queue status polls an agent uses to track long-running work.
/// </summary>
[Collection(E2eCollection.Name)]
public class ProjectApiTests(E2eAppFixture app)
{
    private static readonly HttpClient Http = new();

    private static MultipartFormDataContent CreateForm(string title, string fileName = "book.txt",
        string bookTitle = "A Book", string author = "An Author",
        string text = "Chapter 1\n\nIt was a dark and stormy night.\n\n“Hello,” she said.")
    {
        var form = new MultipartFormDataContent
        {
            { new StringContent(title), "title" },
            { new StringContent(bookTitle), "bookTitle" },
            { new StringContent(author), "author" },
        };
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(text));
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain");
        form.Add(file, "file", fileName);
        return form;
    }

    [Fact]
    public async Task List_projects_returns_json_array()
    {
        var response = await Http.GetAsync($"{app.BaseUrl}/api/projects");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
    }

    [Fact]
    public async Task Create_import_detail_roundtrip()
    {
        var title = $"api-rt-{Guid.NewGuid():N}";

        var create = await Http.PostAsync($"{app.BaseUrl}/api/projects", CreateForm(title));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var created = JsonDocument.Parse(await create.Content.ReadAsStringAsync());
        var folder = created.RootElement.GetProperty("folderName").GetString();
        Assert.False(string.IsNullOrEmpty(folder));

        var import = await Http.PostAsJsonAsync(
            $"{app.BaseUrl}/api/projects/{folder}/import", new { reread = false });
        Assert.Equal(HttpStatusCode.OK, import.StatusCode);

        var detail = await Http.GetAsync($"{app.BaseUrl}/api/projects/{folder}");
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        var project = JsonDocument.Parse(await detail.Content.ReadAsStringAsync());
        Assert.Equal("A Book", project.RootElement.GetProperty("bookTitle").GetString());
        Assert.Equal(folder, project.RootElement.GetProperty("folderName").GetString());
    }

    [Fact]
    public async Task Create_duplicate_title_is_422()
    {
        var title = $"api-dup-{Guid.NewGuid():N}";
        var first = await Http.PostAsync($"{app.BaseUrl}/api/projects", CreateForm(title));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var second = await Http.PostAsync($"{app.BaseUrl}/api/projects", CreateForm(title));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, second.StatusCode);
        Assert.Equal("application/problem+json",
            second.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Unknown_project_detail_is_404()
    {
        var response = await Http.GetAsync($"{app.BaseUrl}/api/projects/no-such-project-xyz");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Traversal_folder_name_is_404()
    {
        var response = await Http.GetAsync($"{app.BaseUrl}/api/projects/..%2F..%2Fsecrets");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Import_unknown_project_is_404()
    {
        var response = await Http.PostAsJsonAsync(
            $"{app.BaseUrl}/api/projects/no-such-project-xyz/import", new { reread = false });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Delete_project_removes_it()
    {
        var title = $"api-del-{Guid.NewGuid():N}";
        var create = await Http.PostAsync($"{app.BaseUrl}/api/projects", CreateForm(title));
        var folder = JsonDocument.Parse(await create.Content.ReadAsStringAsync())
            .RootElement.GetProperty("folderName").GetString();

        var delete = await Http.DeleteAsync($"{app.BaseUrl}/api/projects/{folder}");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);

        var detail = await Http.GetAsync($"{app.BaseUrl}/api/projects/{folder}");
        Assert.Equal(HttpStatusCode.NotFound, detail.StatusCode);
    }

    [Fact]
    public async Task Attribution_queue_snapshot_polls()
    {
        var response = await Http.GetAsync($"{app.BaseUrl}/api/attribution/queue");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(doc.RootElement.TryGetProperty("queuedCount", out _));
        Assert.True(doc.RootElement.TryGetProperty("processingCount", out _));
    }

    [Fact]
    public async Task Audio_queue_snapshot_polls()
    {
        var response = await Http.GetAsync($"{app.BaseUrl}/api/audio/queue");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(doc.RootElement.TryGetProperty("queuedCount", out _));
        Assert.True(doc.RootElement.TryGetProperty("estimatedSecondsRemaining", out _));
    }
}
