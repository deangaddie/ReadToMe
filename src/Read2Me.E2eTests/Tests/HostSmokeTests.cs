using System.Net;
using Read2Me.E2eTests.Infrastructure;

namespace Read2Me.E2eTests.Tests;

/// <summary>
/// Plain-HTTP sanity check on the in-proc host — no browser. The root redirect is host routing
/// that no other test reaches; the project list and the OpenAPI document are pinned by
/// <c>ProjectApiTests</c> and <c>OpenApiContractTests</c>.
/// </summary>
public class HostSmokeTests(E2eAppFixture app) : IClassFixture<E2eAppFixture>
{
    [Fact]
    public async Task Root_redirects_to_the_web_app()
    {
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        var response = await http.GetAsync(app.BaseUrl + "/");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/app/", response.Headers.Location?.OriginalString);
    }
}
