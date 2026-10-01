using Read2Me.E2eTests.Infrastructure;

namespace Read2Me.E2eTests.Tests;

/// <summary>
/// Plain-HTTP sanity checks on the in-proc host — no browser. Validates the
/// fixture (Kestrel, workspace, migrations, seeding, routing) cheaply
/// before the Playwright tests run.
/// </summary>
[Collection(E2eCollection.Name)]
public class HostSmokeTests(E2eAppFixture app)
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

        Assert.Equal(System.Net.HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/app/", response.Headers.Location?.OriginalString);
    }
}
