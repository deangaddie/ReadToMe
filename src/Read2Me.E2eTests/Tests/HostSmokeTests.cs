using System.Net;
using System.Text.Json;
using Read2Me.E2eTests.Infrastructure;

namespace Read2Me.E2eTests.Tests;

/// <summary>
/// Plain-HTTP sanity checks on the in-proc host — no browser. Validates the
/// fixture (Kestrel, workspace, migrations, seeding, routing) cheaply
/// before the Playwright tests run.
/// </summary>
public class HostSmokeTests(E2eAppFixture app) : IClassFixture<E2eAppFixture>
{
    [Fact]
    public async Task Seeded_project_is_listed_by_the_api()
    {
        await app.SeedProjectAsync("smoke-book", "Smoke Test Book", "Smokey Author");

        using var http = new HttpClient();
        var json = await http.GetStringAsync(app.BaseUrl + "/api/projects");

        Assert.Contains("Smoke Test Book", json);
        Assert.Contains("Smokey Author", json);
    }

    [Fact]
    public async Task Root_redirects_to_the_web_app()
    {
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        var response = await http.GetAsync(app.BaseUrl + "/");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/app/", response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task Openapi_document_lists_agent_endpoints()
    {
        using var http = new HttpClient();
        var response = await http.GetAsync(app.BaseUrl + "/openapi/v1.json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var paths = doc.RootElement.GetProperty("paths");
        Assert.True(paths.TryGetProperty("/api/projects", out _));
        Assert.True(paths.TryGetProperty("/api/projects/{folder}/commands", out _));
        Assert.True(paths.TryGetProperty("/api/settings/llm", out _));
    }
}
