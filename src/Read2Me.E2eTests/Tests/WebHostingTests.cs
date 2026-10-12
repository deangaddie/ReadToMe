using System.Net;
using Read2Me.E2eTests.Infrastructure;

namespace Read2Me.E2eTests.Tests;

/// <summary>
/// Plain-HTTP checks that the host serves the web bundle under /app (SPA fallback for deep links,
/// cache headers), a friendly not-built page when the bundle is absent, and that nothing outside
/// that prefix falls back to it. The fixture's web root is a throwaway directory, so each test
/// stages exactly the files it needs.
/// </summary>
public class WebHostingTests(E2eAppFixture app) : IClassFixture<E2eAppFixture>
{
    private const string IndexMarker = "<!-- web-stub-index -->";
    // Bun's content hashes are lowercase.
    private const string HashedAsset = "index-kdddnwpm.js";
    private static readonly HttpClient Http = new();

    [Theory]
    [InlineData("/app")]
    [InlineData("/app/")]
    [InlineData("/app/projects/foundation")]
    public async Task Without_bundle_app_routes_return_not_built_page(string path)
    {
        RemoveBundle();

        var response = await Http.GetAsync(app.BaseUrl + path);
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("The web front end has not been built", html);
        Assert.Contains("pwsh scripts/build-web.ps1", html);
        Assert.Contains("bun run dev", html);
        Assert.DoesNotContain("npm run", html);
        Assert.DoesNotContain(IndexMarker, html);
    }

    [Theory]
    [InlineData("/app")]
    [InlineData("/app/")]
    [InlineData("/app/projects/foundation")]
    public async Task With_bundle_app_routes_return_index_uncached(string path)
    {
        StageBundle();
        try
        {
            var response = await Http.GetAsync(app.BaseUrl + path);
            var html = await response.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains(IndexMarker, html);
            Assert.DoesNotContain("has not been built", html);
            Assert.Equal("no-cache", response.Headers.CacheControl?.ToString());
        }
        finally
        {
            RemoveBundle();
        }
    }

    [Fact]
    public async Task Hashed_bundle_assets_are_immutable_and_index_is_not()
    {
        StageBundle();
        try
        {
            var js = await Http.GetAsync(app.BaseUrl + "/app/" + HashedAsset);
            var index = await Http.GetAsync(app.BaseUrl + "/app/index.html");

            Assert.Equal(HttpStatusCode.OK, js.StatusCode);
            Assert.Equal("public, max-age=31536000, immutable", js.Headers.CacheControl?.ToString());
            Assert.Equal(HttpStatusCode.OK, index.StatusCode);
            Assert.Equal("no-cache", index.Headers.CacheControl?.ToString());
        }
        finally
        {
            RemoveBundle();
        }
    }

    [Fact]
    public async Task Api_is_untouched_by_the_app_fallback()
    {
        StageBundle();
        try
        {
            var api = await Http.GetAsync(app.BaseUrl + "/api/projects");

            Assert.Equal(HttpStatusCode.OK, api.StatusCode);
            Assert.Equal("application/json", api.Content.Headers.ContentType?.MediaType);
        }
        finally
        {
            RemoveBundle();
        }
    }

    /// <summary>
    /// Retired front ends are gone for good: the Blazor UI's routes and assets, and the native
    /// app's migration prefix <c>/app2</c> (ADR 0014). No page, no redirect, and no fall-through to
    /// the web index. Only <c>/</c> still sends a browser to <c>/app/</c>.
    /// </summary>
    [Theory]
    [InlineData("/app2")]
    [InlineData("/app2/projects/foundation")]
    [InlineData("/blazor")]
    [InlineData("/project/foundation")]
    [InlineData("/llm-settings")]
    [InlineData("/llm-prompts")]
    [InlineData("/audio-processing-settings")]
    [InlineData("/_blazor")]
    [InlineData("/_framework/blazor.server.js")]
    [InlineData("/_content/MudBlazor/MudBlazor.min.css")]
    [InlineData("/css/app.css")]
    public async Task Retired_front_end_routes_are_not_found(string path)
    {
        StageBundle();
        try
        {
            using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
            var response = await http.GetAsync(app.BaseUrl + path);

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.DoesNotContain(IndexMarker, await response.Content.ReadAsStringAsync());
        }
        finally
        {
            RemoveBundle();
        }
    }

    private void StageBundle()
    {
        var dir = Path.Combine(app.WebRootDir, "app");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "index.html"),
            $"<!doctype html><html><head><base href=\"/app/\"></head><body>{IndexMarker}<app-root></app-root></body></html>");
        File.WriteAllText(Path.Combine(dir, HashedAsset), "console.log('stub');");
    }

    private void RemoveBundle()
    {
        var dir = Path.Combine(app.WebRootDir, "app");
        if (Directory.Exists(dir))
            Directory.Delete(dir, recursive: true);
    }
}
