using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Read2Me.E2eTests.Infrastructure;

namespace Read2Me.E2eTests.Tests.Api;

/// <summary>
/// Assembly over HTTP: blocked start reports the missing-audio count, status has the
/// polling shape, cancel is safe when idle. The full encode path needs real ffmpeg.
/// </summary>
[Collection(E2eCollection.Name)]
public class AssemblyApiTests(E2eAppFixture app)
{
    private static readonly HttpClient Http = new();

    [Fact]
    public async Task Start_with_missing_audio_is_409_with_remaining_count()
    {
        var folder = $"api-asm-{Guid.NewGuid():N}";
        await app.SeedProjectAsync(folder, "Asm Book", "Author");

        var response = await Http.PostAsJsonAsync(
            $"{app.BaseUrl}/api/projects/{folder}/assembly", new { allowPartial = false });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(problem.RootElement.GetProperty("audioRemainingCount").GetInt32() > 0);
    }

    [Fact]
    public async Task Status_reports_idle_shape()
    {
        var status = JsonDocument.Parse(
            await Http.GetStringAsync($"{app.BaseUrl}/api/assembly/status"));

        Assert.True(status.RootElement.TryGetProperty("isRunning", out _));
        Assert.True(status.RootElement.TryGetProperty("encodePercent", out _));
        Assert.True(status.RootElement.TryGetProperty("currentPhase", out _));
    }

    [Fact]
    public async Task Cancel_when_idle_is_ok()
    {
        var response = await Http.PostAsync($"{app.BaseUrl}/api/assembly/cancel", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Start_unknown_folder_is_404()
    {
        var response = await Http.PostAsJsonAsync(
            $"{app.BaseUrl}/api/projects/nope-asm/assembly", new { allowPartial = false });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Status_names_the_folder_and_output_file_shape()
    {
        var status = JsonDocument.Parse(
            await Http.GetStringAsync($"{app.BaseUrl}/api/assembly/status"));

        Assert.True(status.RootElement.TryGetProperty("folder", out _));
        Assert.True(status.RootElement.TryGetProperty("outputFileName", out _));
    }

    private string SeedOutput(string folder, string fileName, byte[] bytes)
    {
        var dir = Path.Combine(app.WorkspaceDir, folder, "output");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, fileName);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [Fact]
    public async Task Outputs_lists_m4b_files_newest_first_with_the_partial_flag()
    {
        var folder = $"api-asm-{Guid.NewGuid():N}";
        await app.SeedProjectAsync(folder, "Asm Book", "Author");
        File.SetLastWriteTimeUtc(SeedOutput(folder, "Asm Book.m4b", new byte[10]), DateTime.UtcNow.AddDays(-1));
        SeedOutput(folder, "Asm Book_partial_20260919.m4b", new byte[3]);
        SeedOutput(folder, "Asm Book.m4b.tmp", new byte[1]);

        var outputs = JsonDocument.Parse(await Http.GetStringAsync(
            $"{app.BaseUrl}/api/projects/{folder}/assembly/outputs")).RootElement;

        Assert.Equal(2, outputs.GetArrayLength());
        Assert.Equal("Asm Book_partial_20260919.m4b", outputs[0].GetProperty("fileName").GetString());
        Assert.True(outputs[0].GetProperty("isPartial").GetBoolean());
        Assert.Equal("Asm Book.m4b", outputs[1].GetProperty("fileName").GetString());
        Assert.Equal(10, outputs[1].GetProperty("sizeBytes").GetInt64());
        Assert.False(outputs[1].GetProperty("isPartial").GetBoolean());
        Assert.True(outputs[1].GetProperty("createdAt").GetDateTimeOffset() < DateTimeOffset.UtcNow.AddHours(-23));
    }

    [Fact]
    public async Task Outputs_without_an_output_folder_is_an_empty_list()
    {
        var folder = $"api-asm-{Guid.NewGuid():N}";
        await app.SeedProjectAsync(folder, "Asm Book", "Author");

        var outputs = JsonDocument.Parse(await Http.GetStringAsync(
            $"{app.BaseUrl}/api/projects/{folder}/assembly/outputs")).RootElement;

        Assert.Equal(0, outputs.GetArrayLength());
    }

    [Fact]
    public async Task Download_is_an_mp4_attachment()
    {
        var folder = $"api-asm-{Guid.NewGuid():N}";
        await app.SeedProjectAsync(folder, "Asm Book", "Author");
        var bytes = Enumerable.Range(0, 64).Select(i => (byte)i).ToArray();
        SeedOutput(folder, "Asm Book.m4b", bytes);

        var response = await Http.GetAsync(
            $"{app.BaseUrl}/api/projects/{folder}/assembly/outputs/{Uri.EscapeDataString("Asm Book.m4b")}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("audio/mp4", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Contains("Asm Book.m4b", response.Content.Headers.ContentDisposition?.ToString());
        Assert.Equal("bytes", response.Headers.AcceptRanges.Single());
        Assert.Equal(bytes, await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Download_honours_a_range_request()
    {
        var folder = $"api-asm-{Guid.NewGuid():N}";
        await app.SeedProjectAsync(folder, "Asm Book", "Author");
        var bytes = Enumerable.Range(0, 64).Select(i => (byte)i).ToArray();
        SeedOutput(folder, "book.m4b", bytes);

        var request = new HttpRequestMessage(
            HttpMethod.Get, $"{app.BaseUrl}/api/projects/{folder}/assembly/outputs/book.m4b");
        request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(10, 19);
        var response = await Http.SendAsync(request);

        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal(10, response.Content.Headers.ContentRange?.From);
        Assert.Equal(19, response.Content.Headers.ContentRange?.To);
        Assert.Equal(64, response.Content.Headers.ContentRange?.Length);
        Assert.Equal(bytes[10..20], await response.Content.ReadAsByteArrayAsync());
    }

    [Theory]
    [InlineData("missing.m4b")]
    [InlineData("book.m4b.tmp")]
    [InlineData("..%2Fescape.m4b")]
    [InlineData("..%5Cescape.m4b")]
    public async Task Download_and_delete_refuse_anything_but_an_existing_output(string fileName)
    {
        var folder = $"api-asm-{Guid.NewGuid():N}";
        await app.SeedProjectAsync(folder, "Asm Book", "Author");
        SeedOutput(folder, "book.m4b", new byte[4]);
        SeedOutput(folder, "book.m4b.tmp", new byte[4]);
        // A real file one level up: a traversal that resolved would find it.
        File.WriteAllBytes(Path.Combine(app.WorkspaceDir, folder, "escape.m4b"), new byte[4]);

        var url = $"{app.BaseUrl}/api/projects/{folder}/assembly/outputs/{fileName}";

        Assert.Equal(HttpStatusCode.NotFound, (await Http.GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Http.DeleteAsync(url)).StatusCode);
        Assert.True(File.Exists(Path.Combine(app.WorkspaceDir, folder, "escape.m4b")));
        Assert.True(File.Exists(Path.Combine(app.WorkspaceDir, folder, "output", "book.m4b.tmp")));
    }

    [Fact]
    public async Task Delete_removes_the_output()
    {
        var folder = $"api-asm-{Guid.NewGuid():N}";
        await app.SeedProjectAsync(folder, "Asm Book", "Author");
        var path = SeedOutput(folder, "book.m4b", new byte[4]);

        var response = await Http.DeleteAsync($"{app.BaseUrl}/api/projects/{folder}/assembly/outputs/book.m4b");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task Outputs_of_an_unknown_folder_are_404()
    {
        Assert.Equal(HttpStatusCode.NotFound,
            (await Http.GetAsync($"{app.BaseUrl}/api/projects/nope-asm/assembly/outputs")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await Http.GetAsync($"{app.BaseUrl}/api/projects/nope-asm/assembly/outputs/book.m4b")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await Http.DeleteAsync($"{app.BaseUrl}/api/projects/nope-asm/assembly/outputs/book.m4b")).StatusCode);
    }
}
