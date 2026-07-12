using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Read2Me.E2eTests.Infrastructure;

namespace Read2Me.E2eTests.Tests.Api;

/// <summary>
/// Character discovery over HTTP: one LLM call returns the cast, apply persists it.
/// </summary>
[Collection(E2eCollection.Name)]
public class DiscoveryApiTests(E2eAppFixture app)
{
    private static readonly HttpClient Http = new();

    [Fact]
    public async Task Discover_returns_cast_and_apply_persists_it()
    {
        var folder = $"api-disc-{Guid.NewGuid():N}";
        await app.SeedProjectAsync(folder, "Disc Book", "Author");
        app.FakeAi.LlmReply = _ =>
            """{ "reasoning": "outline scan", "characters": [ { "name": "Link", "aliases": ["Hero of Time"] } ] }""";

        var discover = await Http.PostAsync(
            $"{app.BaseUrl}/api/projects/{folder}/characters/discover", null);
        Assert.Equal(HttpStatusCode.OK, discover.StatusCode);
        var outcome = JsonDocument.Parse(await discover.Content.ReadAsStringAsync());
        Assert.Equal("Ok", outcome.RootElement.GetProperty("status").GetString());
        var characters = outcome.RootElement.GetProperty("characters");
        Assert.Equal("Link", characters[0].GetProperty("name").GetString());

        var apply = await Http.PostAsJsonAsync(
            $"{app.BaseUrl}/api/projects/{folder}/characters/discover/apply",
            new[] { new { name = "Link", aliases = new[] { "Hero of Time" } } });
        Assert.Equal(HttpStatusCode.OK, apply.StatusCode);
        var applied = JsonDocument.Parse(await apply.Content.ReadAsStringAsync());
        Assert.Equal(1, applied.RootElement.GetProperty("applied").GetInt32());

        var list = JsonDocument.Parse(
            await Http.GetStringAsync($"{app.BaseUrl}/api/projects/{folder}/characters"));
        var link = list.RootElement.EnumerateArray()
            .Single(c => c.GetProperty("name").GetString() == "Link");
        Assert.Contains("Hero of Time", link.GetProperty("aliases").EnumerateArray()
            .Select(a => a.GetProperty("name").GetString()));

        // Re-apply is idempotent: same character, no duplicate.
        var reapply = await Http.PostAsJsonAsync(
            $"{app.BaseUrl}/api/projects/{folder}/characters/discover/apply",
            new[] { new { name = "Link", aliases = new[] { "Hero of Time" } } });
        Assert.Equal(HttpStatusCode.OK, reapply.StatusCode);
        var list2 = JsonDocument.Parse(
            await Http.GetStringAsync($"{app.BaseUrl}/api/projects/{folder}/characters"));
        Assert.Single(list2.RootElement.EnumerateArray(),
            c => c.GetProperty("name").GetString() == "Link");
    }

    [Fact]
    public async Task Discover_unknown_folder_is_404()
    {
        var response = await Http.PostAsync(
            $"{app.BaseUrl}/api/projects/nope-disc/characters/discover", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
