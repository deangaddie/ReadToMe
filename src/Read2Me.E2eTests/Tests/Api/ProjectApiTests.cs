using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Read2Me.E2eTests.Infrastructure;

namespace Read2Me.E2eTests.Tests.Api;

/// <summary>
/// Vertical slice of the agent-facing API: project list/create/detail/delete,
/// import, patch and cover. The queue status polls are proven by every test that drains a queue.
/// </summary>
public class ProjectApiTests(E2eAppFixture app) : IClassFixture<E2eAppFixture>
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
        Assert.Equal(HttpStatusCode.NoContent, import.StatusCode);

        var detail = await Http.GetAsync($"{app.BaseUrl}/api/projects/{folder}");
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        var project = JsonDocument.Parse(await detail.Content.ReadAsStringAsync());
        Assert.Equal("A Book", project.RootElement.GetProperty("bookTitle").GetString());
        Assert.Equal(folder, project.RootElement.GetProperty("folderName").GetString());
    }

    /// <summary>
    /// The manual reread binds its form and answers 204. How the rules split the book is
    /// ManualBookReaderTests'; the form's parsing is ManualImportRequestTests'.
    /// </summary>
    [Fact]
    public async Task Manual_import_answers_204_and_the_book_has_content()
    {
        var create = await Http.PostAsync($"{app.BaseUrl}/api/projects", CreateForm($"api-manual-{Guid.NewGuid():N}",
            text: "I\n\nThe first chapter.\n\nII\n\nThe second chapter."));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var folder = JsonDocument.Parse(await create.Content.ReadAsStringAsync())
            .RootElement.GetProperty("folderName").GetString()!;

        var manual = await Http.PostAsJsonAsync($"{app.BaseUrl}/api/projects/{folder}/import/manual", new
        {
            hasMultipleVolumes = false,
            hasMultipleParts = false,
            chapter = new { mode = "Roman" },
        });
        Assert.Equal(HttpStatusCode.NoContent, manual.StatusCode);

        Assert.True((await BookAsync(folder)).GetProperty("hasContent").GetBoolean());
    }

    [Fact]
    public async Task Manual_import_rejects_a_bad_form_and_an_unknown_project()
    {
        var folder = await CreateProjectAsync("api-manual-bad");

        var blankPrefix = await Http.PostAsJsonAsync($"{app.BaseUrl}/api/projects/{folder}/import/manual", new
        {
            hasMultipleVolumes = true,
            hasMultipleParts = false,
            volume = new { mode = "Prefix", prefix = "  " },
            chapter = new { mode = "Arabic" },
        });
        Assert.Equal(HttpStatusCode.BadRequest, blankPrefix.StatusCode);
        Assert.Equal("application/problem+json", blankPrefix.Content.Headers.ContentType?.MediaType);
        Assert.False((await BookAsync(folder)).GetProperty("hasContent").GetBoolean());

        var missing = await Http.PostAsJsonAsync(
            $"{app.BaseUrl}/api/projects/no-such-project-xyz/import/manual",
            new { hasMultipleVolumes = false, hasMultipleParts = false, chapter = new { mode = "Roman" } });
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    private Task<JsonElement> BookAsync(string folder) => GetJsonAsync($"/api/projects/{folder}/book");

    private async Task<JsonElement> GetJsonAsync(string path)
    {
        var response = await Http.GetAsync($"{app.BaseUrl}{path}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    [Fact]
    public async Task Create_duplicate_title_is_409()
    {
        var title = $"api-dup-{Guid.NewGuid():N}";
        var first = await Http.PostAsync($"{app.BaseUrl}/api/projects", CreateForm(title));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var second = await Http.PostAsync($"{app.BaseUrl}/api/projects", CreateForm(title));
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
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

    private async Task<string> CreateProjectAsync(string prefix)
    {
        var create = await Http.PostAsync($"{app.BaseUrl}/api/projects", CreateForm($"{prefix}-{Guid.NewGuid():N}"));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        return JsonDocument.Parse(await create.Content.ReadAsStringAsync())
            .RootElement.GetProperty("folderName").GetString()!;
    }

    private async Task<JsonElement> DetailAsync(string folder)
    {
        var detail = await Http.GetAsync($"{app.BaseUrl}/api/projects/{folder}");
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        return JsonDocument.Parse(await detail.Content.ReadAsStringAsync()).RootElement;
    }

    private static MultipartFormDataContent CoverForm(string fileName, int bytes = 16)
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(new byte[bytes]);
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
        form.Add(file, "file", fileName);
        return form;
    }

    [Fact]
    public async Task Patch_updates_only_the_fields_sent()
    {
        var folder = await CreateProjectAsync("api-patch");

        var patch = await Http.PatchAsJsonAsync(
            $"{app.BaseUrl}/api/projects/{folder}", new { title = " Renamed ", author = "New Author" });
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);
        var body = JsonDocument.Parse(await patch.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("Renamed", body.GetProperty("title").GetString());
        Assert.Equal("New Author", body.GetProperty("author").GetString());
        Assert.Equal("A Book", body.GetProperty("bookTitle").GetString());
        Assert.Equal(folder, body.GetProperty("folderName").GetString());

        var detail = await DetailAsync(folder);
        Assert.Equal("Renamed", detail.GetProperty("title").GetString());
    }

    [Fact]
    public async Task Patch_blank_title_is_400_and_unknown_project_404()
    {
        var folder = await CreateProjectAsync("api-patch-blank");

        var blank = await Http.PatchAsJsonAsync($"{app.BaseUrl}/api/projects/{folder}", new { title = "   " });
        Assert.Equal(HttpStatusCode.BadRequest, blank.StatusCode);

        var missing = await Http.PatchAsJsonAsync(
            $"{app.BaseUrl}/api/projects/no-such-project-xyz", new { title = "x" });
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task Narrator_only_mode_round_trips()
    {
        var folder = await CreateProjectAsync("api-nom");
        Assert.False((await DetailAsync(folder)).GetProperty("narratorOnlyMode").GetBoolean());

        var on = await Http.PutAsJsonAsync(
            $"{app.BaseUrl}/api/projects/{folder}/narrator-only-mode", new { enabled = true });
        Assert.Equal(HttpStatusCode.NoContent, on.StatusCode);
        Assert.True((await DetailAsync(folder)).GetProperty("narratorOnlyMode").GetBoolean());

        var off = await Http.PutAsJsonAsync(
            $"{app.BaseUrl}/api/projects/{folder}/narrator-only-mode", new { enabled = false });
        Assert.Equal(HttpStatusCode.NoContent, off.StatusCode);
        Assert.False((await DetailAsync(folder)).GetProperty("narratorOnlyMode").GetBoolean());
    }

    [Fact]
    public async Task Cover_upload_shows_on_detail_and_shelf_then_delete_clears_it()
    {
        var folder = await CreateProjectAsync("api-cover");

        var upload = await Http.PutAsync($"{app.BaseUrl}/api/projects/{folder}/cover", CoverForm("cover.png"));
        Assert.Equal(HttpStatusCode.OK, upload.StatusCode);
        var body = JsonDocument.Parse(await upload.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("cover.png", body.GetProperty("coverImage").GetString());

        Assert.Equal("cover.png", (await DetailAsync(folder)).GetProperty("coverImage").GetString());

        var list = await Http.GetAsync($"{app.BaseUrl}/api/projects");
        var summaries = JsonDocument.Parse(await list.Content.ReadAsStringAsync()).RootElement;
        var mine = summaries.EnumerateArray().Single(p => p.GetProperty("folderName").GetString() == folder);
        Assert.Equal("cover.png", mine.GetProperty("coverImage").GetString());
        Assert.Equal("Text", mine.GetProperty("fileType").GetString());

        var served = await Http.GetAsync($"{app.BaseUrl}/workspace/{folder}/cover.png");
        Assert.Equal(HttpStatusCode.OK, served.StatusCode);

        var delete = await Http.DeleteAsync($"{app.BaseUrl}/api/projects/{folder}/cover");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        Assert.Equal(JsonValueKind.Null, (await DetailAsync(folder)).GetProperty("coverImage").ValueKind);
    }

    /// <summary>
    /// The cover's 400s. No unit test pins them: wrong type and over 10 MB are the endpoint's own
    /// checks, and no file and not a form are its binding.
    /// </summary>
    [Theory]
    [InlineData("wrong type")]
    [InlineData("over 10 MB")]
    [InlineData("no file")]
    [InlineData("not a form")]
    public async Task Cover_rejects_a_bad_form_and_saves_nothing(string form)
    {
        var folder = await CreateProjectAsync("api-cover-bad");
        HttpContent content = form switch
        {
            "wrong type" => CoverForm("cover.gif"),
            "over 10 MB" => CoverForm("cover.png", 10 * 1024 * 1024 + 1),
            "no file" => new MultipartFormDataContent { { new StringContent("x"), "note" } },
            "not a form" => JsonContent.Create(new { file = "x" }),
            _ => throw new ArgumentOutOfRangeException(nameof(form), form, "No such form row."),
        };

        var response = await Http.PutAsync($"{app.BaseUrl}/api/projects/{folder}/cover", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(JsonValueKind.Null, (await DetailAsync(folder)).GetProperty("coverImage").ValueKind);
    }

    [Fact]
    public async Task Cover_of_an_unknown_project_is_404()
    {
        var missing = await Http.PutAsync(
            $"{app.BaseUrl}/api/projects/no-such-project-xyz/cover", CoverForm("cover.png"));

        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }
}
