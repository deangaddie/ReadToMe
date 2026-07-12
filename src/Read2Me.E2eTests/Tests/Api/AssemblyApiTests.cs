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
}
