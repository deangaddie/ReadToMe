using System.Net;
using System.Text;
using System.Text.Json;
using Read2Me.E2eTests.Infrastructure;

namespace Read2Me.E2eTests.Tests.Api;

/// <summary>
/// The one wire form (#166, #168), strict in both directions: an enum is its PascalCase member name in a
/// body, a route segment and a dictionary key, and a number is a JSON number. An integer for an enum or a
/// string for a number is a 400, whether the body is bound or read by hand. A route segment must match
/// the member name exactly (a step that is not one is a 404); a body's name is read ignoring case, as
/// System.Text.Json reads every enum name.
/// </summary>
public class WireFormApiTests(E2eAppFixture app) : IClassFixture<E2eAppFixture>
{
    private static readonly HttpClient Http = new();

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    private async Task<string> SeedAsync()
    {
        var folder = $"api-wire-{Guid.NewGuid():N}";
        await app.SeedProjectAsync(folder, "Wire Book", "Author");
        return folder;
    }

    [Fact]
    public async Task An_enum_sent_as_an_integer_is_a_400()
    {
        var folder = await SeedAsync();

        var bound = await Http.PutAsync($"{app.BaseUrl}/api/projects/{folder}/checkpoints/Structure",
            Json("""{ "state": 0 }"""), TestContext.Current.CancellationToken);
        var enqueue = await Http.PostAsync($"{app.BaseUrl}/api/projects/{folder}/attribution/enqueue",
            Json($$"""{ "level": 2, "nodeId": "{{Guid.NewGuid()}}" }"""), TestContext.Current.CancellationToken);
        var byHand = await Http.PostAsync($"{app.BaseUrl}/api/settings/llm",
            Json("""{ "name": "Local", "baseUrl": "http://localhost:8080", "apiType": 0 }"""), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, bound.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, enqueue.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, byHand.StatusCode);
    }

    [Fact]
    public async Task A_route_segment_in_another_casing_is_refused()
    {
        var folder = await SeedAsync();

        var step = await Http.PutAsync($"{app.BaseUrl}/api/projects/{folder}/checkpoints/structure",
            Json("""{ "state": "Confirmed" }"""), TestContext.Current.CancellationToken);
        var level = await Http.GetAsync($"{app.BaseUrl}/api/projects/{folder}/nodes/chapter/{Guid.NewGuid()}/item-ids",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, step.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, level.StatusCode);
    }

    [Fact]
    public async Task A_number_sent_as_a_string_is_a_400()
    {
        var byHand = await Http.PutAsync($"{app.BaseUrl}/api/settings/llm/active",
            Json("""{ "id": "1" }"""), TestContext.Current.CancellationToken);
        var bound = await Http.PutAsync($"{app.BaseUrl}/api/settings/audio-processing",
            Json("""{ "audioMaxAttempts": "3" }"""), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, byHand.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, bound.StatusCode);
    }

    [Fact]
    public async Task Enums_go_out_as_member_names()
    {
        var folder = await SeedAsync();

        var project = JsonDocument.Parse(await Http.GetStringAsync($"{app.BaseUrl}/api/projects/{folder}",
            TestContext.Current.CancellationToken)).RootElement;
        var report = JsonDocument.Parse(await Http.GetStringAsync($"{app.BaseUrl}/api/setup-check",
            TestContext.Current.CancellationToken)).RootElement;

        Assert.Equal(JsonValueKind.String, project.GetProperty("fileType").ValueKind);
        var row = report.GetProperty("rows").EnumerateArray().First();
        Assert.Contains(row.GetProperty("group").GetString(), new[] { "WorkspaceAndTools", "AiConfigs", "DockerServices" });
        Assert.Contains(row.GetProperty("tier").GetString(), new[] { "Quick", "Full" });
    }
}
