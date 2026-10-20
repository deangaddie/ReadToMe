using System.Net;
using System.Text;
using System.Text.Json;
using Read2Me.E2eTests.Infrastructure;

namespace Read2Me.E2eTests.Tests.Api;

/// <summary>
/// <c>GET /api/projects/{folder}/history</c>: the contract only. What History records is proved
/// over a real project in the History module's own tests.
/// </summary>
public class HistoryApiTests(E2eAppFixture app) : IClassFixture<E2eAppFixture>
{
    private static readonly HttpClient Http = new();

    private async Task<JsonElement> HistoryAsync(string folder) =>
        JsonDocument.Parse(await Http.GetStringAsync($"{app.BaseUrl}/api/projects/{folder}/history")).RootElement;

    [Fact]
    public async Task History_lists_steps_newest_first_with_the_head_and_the_busy_answer()
    {
        var folder = $"api-history-{Guid.NewGuid():N}";
        await app.SeedProjectAsync(folder, "History Book", "Author");

        var started = await HistoryAsync(folder);
        var only = Assert.Single(started.GetProperty("steps").EnumerateArray());
        Assert.Equal("History started", only.GetProperty("label").GetString());

        var created = await Http.PostAsync($"{app.BaseUrl}/api/projects/{folder}/commands",
            new StringContent("""{ "type": "CreateCharacter", "name": "Ganondorf" }""", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);

        var history = await HistoryAsync(folder);
        var steps = history.GetProperty("steps").EnumerateArray().ToList();
        Assert.Equal(["Create character", "History started"], steps.Select(s => s.GetProperty("label").GetString()));

        var tip = steps[0];
        Assert.Equal(tip.GetProperty("stepId").GetGuid(), history.GetProperty("headStepId").GetGuid());
        Assert.Equal(steps[1].GetProperty("stepId").GetGuid(), tip.GetProperty("parentStepId").GetGuid());
        Assert.Equal(2, tip.GetProperty("sequence").GetInt32());
        Assert.False(tip.GetProperty("isOpen").GetBoolean());
        Assert.False(string.IsNullOrEmpty(tip.GetProperty("author").GetString()));
        Assert.True(tip.TryGetProperty("startedAt", out _));
        Assert.True(tip.TryGetProperty("endedAt", out _));
        Assert.Equal(JsonValueKind.Null, history.GetProperty("busy").ValueKind);
        Assert.False(history.GetProperty("restoring").GetBoolean());
    }

    [Fact]
    public async Task History_of_an_unknown_project_is_404()
    {
        var response = await Http.GetAsync($"{app.BaseUrl}/api/projects/no-such-{Guid.NewGuid():N}/history");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
