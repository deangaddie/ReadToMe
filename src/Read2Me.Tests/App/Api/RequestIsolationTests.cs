using Read2Me.App.Api;
using Xunit;

namespace Read2Me.Tests.App.Api;

/// <summary>
/// The host's request isolation rule (security guide §3.7): Fetch Metadata first, an exact
/// <c>Origin</c> match when a browser sends none, and callers with no browser headers left alone.
/// </summary>
public sealed class RequestIsolationTests
{
    private static readonly HashSet<string> Own = ["http://localhost:5000", "https://localhost:5001"];

    private static bool Allows(string method = "GET", string? site = null, string? mode = null,
        string? dest = null, string? origin = null) =>
        RequestIsolation.Allows(new FetchMetadata(method, site, mode, dest, origin), Own);

    [Fact]
    public void A_caller_with_no_browser_headers_is_allowed() =>
        Assert.True(Allows("POST"));

    [Theory]
    [InlineData("same-origin")]
    [InlineData("none")]
    public void Same_origin_and_user_initiated_requests_are_allowed(string site) =>
        Assert.True(Allows("POST", site, "cors", "empty"));

    [Theory]
    [InlineData("cross-site", "POST", "no-cors", "empty")]   // a form or a bodyless fetch from another site
    [InlineData("cross-site", "GET", "websocket", "websocket")]
    [InlineData("cross-site", "GET", "no-cors", "audio")]
    [InlineData("same-site", "POST", "no-cors", "empty")]    // another localhost port is the same site
    [InlineData("same-site", "GET", "websocket", "websocket")]
    [InlineData("cross-site", "POST", "navigate", "document")] // a form post is a navigation, but not a GET
    [InlineData("cross-site", "GET", "navigate", "object")]  // an <object> loads as a navigation
    [InlineData("cross-site", "GET", "navigate", "embed")]
    [InlineData("something-new", "GET", "cors", "empty")]
    public void Other_sites_are_refused_except_for_a_top_level_GET_navigation(
        string site, string method, string mode, string dest) =>
        Assert.False(Allows(method, site, mode, dest));

    [Theory]
    [InlineData("cross-site", "document")]
    [InlineData("same-site", "document")]
    [InlineData("cross-site", "iframe")]
    public void A_GET_navigation_from_another_site_is_allowed(string site, string dest) =>
        Assert.True(Allows("GET", site, "navigate", dest));

    [Theory]
    [InlineData("http://localhost:5000")]
    [InlineData("https://localhost:5001")]
    public void Without_fetch_metadata_one_of_the_hosts_own_origins_is_allowed(string origin) =>
        Assert.True(Allows("POST", origin: origin));

    [Theory]
    [InlineData("http://localhost:5000.evil.test")] // only starts with an own origin
    [InlineData("http://localhost:50001")]
    [InlineData("http://localhost:4301")]
    [InlineData("https://localhost:5000")]           // the scheme counts
    [InlineData("http://LOCALHOST:5000")]            // browsers serialise lowercase; anything else is not one
    [InlineData("http://localhost:5000/")]
    [InlineData("null")]
    [InlineData("")]
    public void Without_fetch_metadata_any_other_origin_is_refused(string origin) =>
        Assert.False(Allows("POST", origin: origin));

    [Fact]
    public void A_loopback_address_stands_for_each_loopback_name_a_browser_can_use() =>
        Assert.Equal(
            ["http://127.0.0.1:5000", "http://[::1]:5000", "http://localhost:5000",
             "https://127.0.0.1:5001", "https://[::1]:5001", "https://localhost:5001"],
            RequestIsolation.OwnOrigins(["http://localhost:5000", "https://127.0.0.1:5001"], development: false).Order(StringComparer.Ordinal));

    [Fact]
    public void A_wildcard_address_is_reached_on_loopback() =>
        Assert.Equal(
            ["http://127.0.0.1:5000", "http://127.0.0.1:5002", "http://127.0.0.1:5003",
             "http://[::1]:5000", "http://[::1]:5002", "http://[::1]:5003",
             "http://localhost:5000", "http://localhost:5002", "http://localhost:5003"],
            RequestIsolation.OwnOrigins(["http://[::]:5000", "http://+:5002", "http://0.0.0.0:5003"], development: false).Order(StringComparer.Ordinal));

    [Fact]
    public void A_named_address_is_its_own_origin_and_a_default_port_is_left_out() =>
        Assert.Equal(
            ["http://reader.lan", "https://reader.lan:8443"],
            RequestIsolation.OwnOrigins(["http://Reader.lan:80", "https://reader.lan:8443/"], development: false).Order(StringComparer.Ordinal));

    [Fact]
    public void Development_adds_the_web_dev_server() =>
        Assert.Equal(
            ["http://127.0.0.1:4300", "http://127.0.0.1:5000", "http://[::1]:5000", "http://localhost:4300", "http://localhost:5000"],
            RequestIsolation.OwnOrigins(["http://localhost:5000"], development: true).Order(StringComparer.Ordinal));

    [Fact]
    public void Fetch_metadata_decides_over_an_origin_header() =>
        Assert.False(Allows("POST", "cross-site", "cors", "empty", origin: "http://localhost:5000"));
}
