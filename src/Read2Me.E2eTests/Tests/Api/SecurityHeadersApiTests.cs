using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Read2Me.App.Api;
using Read2Me.E2eTests.Infrastructure;
using Read2Me.Services.Audio;

namespace Read2Me.E2eTests.Tests.Api;

/// <summary>
/// Every response the host sends carries the frame, sniff, opener and resource-policy headers
/// (modern-web-guidance security/security §1.4 and its companion policies): pages, bundle assets,
/// API JSON, Workspace files, audio previews, and the 404s and 403s too.
/// </summary>
public class SecurityHeadersApiTests(E2eAppFixture app) : IClassFixture<E2eAppFixture>, IAsyncLifetime
{
    private const string HashedAsset = "index-kdddnwpm.js";
    private const string PreviewToken = "security-headers-preview";
    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false });

    public async ValueTask InitializeAsync()
    {
        var bundle = Path.Combine(app.WebRootDir, "app");
        Directory.CreateDirectory(bundle);
        await File.WriteAllTextAsync(Path.Combine(bundle, "index.html"), "<!doctype html><app-root></app-root>");
        await File.WriteAllTextAsync(Path.Combine(bundle, HashedAsset), "console.log('stub');");
        await File.WriteAllTextAsync(Path.Combine(app.WorkspaceDir, "headers.txt"), "Call me Ishmael.");
        await app.Services.GetRequiredService<AudioPreviewStore>().SaveAsync(PreviewToken, [1, 2, 3]);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Theory]
    [InlineData("/app/projects/moby-dick", HttpStatusCode.OK)]  // the index, through the SPA fallback
    [InlineData("/app/" + HashedAsset, HttpStatusCode.OK)]
    [InlineData("/api/projects", HttpStatusCode.OK)]
    [InlineData("/workspace/headers.txt", HttpStatusCode.OK)]
    [InlineData("/audio-preview/" + PreviewToken, HttpStatusCode.OK)]
    [InlineData("/", HttpStatusCode.Redirect)]
    [InlineData("/no-such-page", HttpStatusCode.NotFound)]
    [InlineData("/workspace/no-such-file.wav", HttpStatusCode.NotFound)]
    public async Task Every_response_carries_the_four_headers(string path, HttpStatusCode status)
    {
        var response = await Http.GetAsync(app.BaseUrl + path);

        Assert.Equal(status, response.StatusCode);
        AssertHeaders(response);
    }

    [Fact]
    public async Task A_refused_cross_site_request_carries_them_too()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, app.BaseUrl + "/workspace/headers.txt");
        request.Headers.Add("Sec-Fetch-Site", "cross-site");
        request.Headers.Add("Sec-Fetch-Mode", "no-cors");
        request.Headers.Add("Sec-Fetch-Dest", "image");

        var response = await Http.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        AssertHeaders(response);
    }

    /// <summary>
    /// The exception handler clears the response before writing its problem, headers included. The
    /// host's endpoints don't throw on demand, so a slim app with the same two middlewares stands in.
    /// </summary>
    [Fact]
    public async Task An_exception_handlers_500_keeps_them()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddProblemDetails();
        await using var web = builder.Build();
        web.UseSecurityHeaders();
        web.UseExceptionHandler();
        web.MapGet("/boom", () => { throw new InvalidOperationException("boom"); });
        await web.StartAsync(TestContext.Current.CancellationToken);
        var url = web.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();

        var response = await Http.GetAsync(url + "/boom", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        AssertHeaders(response);
    }

    private static void AssertHeaders(HttpResponseMessage response)
    {
        Assert.Equal("DENY", Single(response, "X-Frame-Options"));
        Assert.Equal("nosniff", Single(response, "X-Content-Type-Options"));
        Assert.Equal("same-origin", Single(response, "Cross-Origin-Opener-Policy"));
        Assert.Equal("same-origin", Single(response, "Cross-Origin-Resource-Policy"));
    }

    private static string Single(HttpResponseMessage response, string name) =>
        Assert.Single(response.Headers.TryGetValues(name, out var values) ? values : []);
}
