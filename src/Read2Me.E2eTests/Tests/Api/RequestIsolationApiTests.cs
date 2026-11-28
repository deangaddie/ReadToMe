using System.Net;
using Read2Me.E2eTests.Infrastructure;

namespace Read2Me.E2eTests.Tests.Api;

/// <summary>
/// The host refuses another page's requests to its API, live hub and Workspace files before they
/// reach a route, and leaves callers with no browser headers alone. The rule itself is pinned by
/// <c>RequestIsolationTests</c>; this proves where the host applies it.
/// </summary>
public class RequestIsolationApiTests(E2eAppFixture app) : IClassFixture<E2eAppFixture>
{
    private static readonly HttpClient Http = new();

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? site = null,
        string? mode = null, string? dest = null, string? origin = null)
    {
        using var request = new HttpRequestMessage(method, app.BaseUrl + path);
        if (site is not null) request.Headers.Add("Sec-Fetch-Site", site);
        if (mode is not null) request.Headers.Add("Sec-Fetch-Mode", mode);
        if (dest is not null) request.Headers.Add("Sec-Fetch-Dest", dest);
        if (origin is not null) request.Headers.Add("Origin", origin);
        return await Http.SendAsync(request);
    }

    [Theory]
    [InlineData("/api/attribution/cancel")]
    [InlineData("/hubs/live/negotiate?negotiateVersion=1")]
    public async Task Another_localhost_port_is_refused_on_the_API_and_the_hub(string path)
    {
        var response = await SendAsync(HttpMethod.Post, path, "same-site", "no-cors", "empty", "http://localhost:4301");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(["Sec-Fetch-Dest", "Sec-Fetch-Mode", "Sec-Fetch-Site"], response.Headers.Vary);
    }

    [Fact]
    public async Task An_origin_that_only_starts_with_the_hosts_own_is_refused()
    {
        var own = await SendAsync(HttpMethod.Post, "/api/attribution/cancel", origin: app.BaseUrl);
        var prefixed = await SendAsync(HttpMethod.Post, "/api/attribution/cancel", origin: app.BaseUrl + ".evil.test");

        Assert.True(own.IsSuccessStatusCode, $"own origin got {own.StatusCode}");
        Assert.Equal(HttpStatusCode.Forbidden, prefixed.StatusCode);
    }

    [Fact]
    public async Task A_Workspace_file_opens_from_a_link_on_another_site_but_does_not_load_into_its_page()
    {
        await File.WriteAllTextAsync(Path.Combine(app.WorkspaceDir, "isolation.txt"), "Call me Ishmael.");

        var navigation = await SendAsync(HttpMethod.Get, "/workspace/isolation.txt", "cross-site", "navigate", "document");
        var subresource = await SendAsync(HttpMethod.Get, "/workspace/isolation.txt", "cross-site", "no-cors", "audio");
        var noHeaders = await SendAsync(HttpMethod.Get, "/workspace/isolation.txt");

        Assert.Equal(HttpStatusCode.OK, navigation.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, subresource.StatusCode);
        Assert.Equal(HttpStatusCode.OK, noHeaders.StatusCode);
        Assert.Contains("Sec-Fetch-Site", navigation.Headers.Vary);
    }
}
