using Read2Me.E2eTests.Infrastructure;

namespace Read2Me.E2eTests.Tests;

/// <summary>
/// Plain-HTTP sanity checks on the in-proc host — no browser. Validates the
/// fixture (Kestrel, workspace, migrations, seeding, static assets) cheaply
/// before the Playwright tests run.
/// </summary>
[Collection(E2eCollection.Name)]
public class HostSmokeTests(E2eAppFixture app)
{
    [Fact]
    public async Task Home_prerenders_seeded_project()
    {
        await app.SeedProjectAsync("smoke-book", "Smoke Test Book", "Smokey Author");

        using var http = new HttpClient();
        var html = await http.GetStringAsync(app.BaseUrl + "/blazor");

        Assert.Contains("Smoke Test Book", html);
        Assert.Contains("Smokey Author", html);
    }

    [Fact]
    public async Task Root_redirects_to_the_web_app()
    {
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        var response = await http.GetAsync(app.BaseUrl + "/");

        Assert.Equal(System.Net.HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/app/", response.Headers.Location?.OriginalString);
    }

    /// <summary>
    /// The prompt editor builds its {{context_json}} previews from the real request serializers, so
    /// a broken sample surfaces as a component initializer throw rather than a wrong string. A plain
    /// prerender is enough to run those initializers.
    /// </summary>
    [Fact]
    public async Task Llm_prompts_page_prerenders()
    {
        using var http = new HttpClient();
        var response = await http.GetAsync(app.BaseUrl + "/llm-prompts");
        var html = await response.Content.ReadAsStringAsync();

        Assert.True(response.IsSuccessStatusCode, $"/llm-prompts: {response.StatusCode}");
        Assert.Contains("LLM Prompts", html);
        Assert.Contains("&quot;items&quot;", html);
        Assert.DoesNotContain("&quot;paragraph&quot;", html);
    }

    [Fact]
    public async Task Static_assets_are_served()
    {
        using var http = new HttpClient();

        var appCss = await http.GetAsync(app.BaseUrl + "/css/app.css");
        var mudCss = await http.GetAsync(app.BaseUrl + "/_content/MudBlazor/MudBlazor.min.css");

        Assert.True(appCss.IsSuccessStatusCode, $"app.css: {appCss.StatusCode}");
        Assert.True(mudCss.IsSuccessStatusCode, $"MudBlazor.min.css: {mudCss.StatusCode}");
    }
}
