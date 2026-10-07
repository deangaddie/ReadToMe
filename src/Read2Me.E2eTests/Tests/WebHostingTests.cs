using System.Net;
using Read2Me.E2eTests.Infrastructure;

namespace Read2Me.E2eTests.Tests;

/// <summary>
/// Plain-HTTP checks that the host serves the Angular bundle under /app and the native bundle under
/// /app2 (SPA fallback for deep links, cache headers), a friendly not-built page for each when its
/// bundle is absent, and that nothing outside those prefixes falls back to them. The fixture's web
/// root is a throwaway directory, so each test stages exactly the files it needs.
/// </summary>
[Collection(E2eCollection.Name)]
public class WebHostingTests(E2eAppFixture app)
{
    private const string IndexMarker = "<!-- angular-stub-index -->";
    private const string NativeIndexMarker = "<!-- native-stub-index -->";
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
        Assert.Contains("npm run build", html);
        Assert.DoesNotContain("<app-root>", html);
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
            Assert.DoesNotContain("npm run build", html);
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
            var js = await Http.GetAsync(app.BaseUrl + "/app/main-UP4C2GUA.js");
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

    [Theory]
    [InlineData("/app2")]
    [InlineData("/app2/")]
    [InlineData("/app2/projects/foundation")]
    public async Task Without_native_bundle_app2_routes_return_native_not_built_page(string path)
    {
        RemoveNativeBundle();

        var response = await Http.GetAsync(app.BaseUrl + path);
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("The native front end has not been built", html);
        Assert.Contains("pwsh scripts/build-native.ps1", html);
        Assert.Contains("bun run dev", html);
        Assert.DoesNotContain("npm run build", html);
        Assert.DoesNotContain(NativeIndexMarker, html);
    }

    /// <summary>
    /// Bun hashes are lowercase (index-kdddnwpm.js), unlike Angular's uppercase ones, and the
    /// immutable rule has to catch both under /app2/ as it does under /app/.
    /// </summary>
    [Fact]
    public async Task Native_bundle_deep_links_serve_index_and_lowercase_hashed_assets_are_immutable()
    {
        StageNativeBundle();
        try
        {
            var deepLink = await Http.GetAsync(app.BaseUrl + "/app2/projects/foundation");
            var js = await Http.GetAsync(app.BaseUrl + "/app2/index-kdddnwpm.js");
            var index = await Http.GetAsync(app.BaseUrl + "/app2/index.html");

            Assert.Equal(HttpStatusCode.OK, deepLink.StatusCode);
            Assert.Contains(NativeIndexMarker, await deepLink.Content.ReadAsStringAsync());
            Assert.Equal("no-cache", deepLink.Headers.CacheControl?.ToString());
            Assert.Equal(HttpStatusCode.OK, js.StatusCode);
            Assert.Equal("public, max-age=31536000, immutable", js.Headers.CacheControl?.ToString());
            Assert.Equal("no-cache", index.Headers.CacheControl?.ToString());
        }
        finally
        {
            RemoveNativeBundle();
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
    /// The retired Blazor UI's routes and assets are gone for good: no page, no redirect, and no
    /// fall-through to the Angular index. Only <c>/</c> still sends a browser to <c>/app/</c>.
    /// </summary>
    [Theory]
    [InlineData("/blazor")]
    [InlineData("/project/foundation")]
    [InlineData("/llm-settings")]
    [InlineData("/llm-prompts")]
    [InlineData("/audio-processing-settings")]
    [InlineData("/_blazor")]
    [InlineData("/_framework/blazor.server.js")]
    [InlineData("/_content/MudBlazor/MudBlazor.min.css")]
    [InlineData("/css/app.css")]
    public async Task Retired_blazor_routes_are_not_found(string path)
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

    // Angular: uppercase hash, <app-root>. Native (Bun): lowercase hash, <r2m-app>.
    private void StageBundle() => StageBundle("app", IndexMarker, "<app-root></app-root>", "main-UP4C2GUA.js");
    private void RemoveBundle() => RemoveBundle("app");
    private void StageNativeBundle() => StageBundle("app2", NativeIndexMarker, "<r2m-app></r2m-app>", "index-kdddnwpm.js");
    private void RemoveNativeBundle() => RemoveBundle("app2");

    private void StageBundle(string folder, string marker, string rootElement, string hashedAsset)
    {
        var dir = Path.Combine(app.WebRootDir, folder);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "index.html"),
            $"<!doctype html><html><head><base href=\"/{folder}/\"></head><body>{marker}{rootElement}</body></html>");
        File.WriteAllText(Path.Combine(dir, hashedAsset), "console.log('stub');");
    }

    private void RemoveBundle(string folder)
    {
        var dir = Path.Combine(app.WebRootDir, folder);
        if (Directory.Exists(dir))
            Directory.Delete(dir, recursive: true);
    }
}
