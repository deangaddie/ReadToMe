using System.Net;
using Read2Me.E2eTests.Infrastructure;

namespace Read2Me.E2eTests.Tests;

/// <summary>
/// Plain-HTTP checks that the host serves the Angular bundle under /app (SPA fallback for deep
/// links, cache headers) and a friendly not-built page when the bundle is absent — without ever
/// falling through to Blazor's _Host. The fixture's web root is a throwaway directory, so each
/// test stages exactly the files it needs.
/// </summary>
[Collection(E2eCollection.Name)]
public class WebHostingTests(E2eAppFixture app)
{
    private const string IndexMarker = "<!-- angular-stub-index -->";
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
        Assert.DoesNotContain("_framework/blazor.server.js", html);
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

    [Fact]
    public async Task Blazor_root_and_api_are_untouched_by_the_app_fallback()
    {
        StageBundle();
        try
        {
            var root = await Http.GetAsync(app.BaseUrl + "/");
            var rootHtml = await root.Content.ReadAsStringAsync();
            var api = await Http.GetAsync(app.BaseUrl + "/api/projects");

            Assert.Equal(HttpStatusCode.OK, root.StatusCode);
            Assert.Contains("_framework/blazor.server.js", rootHtml);
            Assert.DoesNotContain(IndexMarker, rootHtml);
            Assert.Equal(HttpStatusCode.OK, api.StatusCode);
            Assert.Equal("application/json", api.Content.Headers.ContentType?.MediaType);
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
        File.WriteAllText(Path.Combine(dir, "main-UP4C2GUA.js"), "console.log('stub');");
    }

    private void RemoveBundle()
    {
        var dir = Path.Combine(app.WebRootDir, "app");
        if (Directory.Exists(dir))
            Directory.Delete(dir, recursive: true);
    }
}
