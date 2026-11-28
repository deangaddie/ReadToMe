using Read2Me.E2eTests.Infrastructure;

namespace Read2Me.E2eTests.Tests.Web;

/// <summary>
/// THROWAWAY proof for #509, deleted in the PR's final commit: the three injected actions the nonce
/// CSP with Trusted Types must stop, in the running app.
/// </summary>
public class CspProofTests(E2eAppFixture app, PlaywrightFixture pw) : E2eTestBase(app, pw)
{
    [Fact]
    public async Task A_raw_innerHTML_sink_throws_a_TypeError()
    {
        await GotoAppAsync("");

        var outcome = await Page.EvaluateAsync<string>(
            "() => { try { document.body.innerHTML = 'x'; return 'ran: ' + document.body.textContent; } catch (e) { return e.name; } }");
        TakeCspViolations();

        Assert.Equal("TypeError", outcome);
    }

    [Fact]
    public async Task An_inline_script_with_no_nonce_does_not_run()
    {
        await GotoAppAsync("");
        var index = Path.Combine(App.WebRootDir, "app", "index.html");
        var html = await File.ReadAllTextAsync(index);
        await File.WriteAllTextAsync(index, html.Replace("</head>", "<script>window.__injected = 'ran'</script></head>"));

        await Page.GotoAsync(AppPath(""));
        await Expect(Page.Locator("app-root[data-live-state='connected']")).ToBeAttachedAsync(new() { Timeout = 15_000 });
        var injected = await Page.EvaluateAsync<string?>("() => window.__injected ?? null");
        var violations = TakeCspViolations();

        Assert.Null(injected);
        Assert.Contains(violations, v => v.StartsWith("script-src"));
    }

    [Fact]
    public async Task A_fetch_to_another_origin_is_blocked_by_connect_src()
    {
        await GotoAppAsync("");

        var outcome = await Page.EvaluateAsync<string>(
            "() => fetch('http://localhost:8080/').then(r => 'fetched ' + r.status, e => e.name)");
        await Task.Delay(200); // the violation event is queued after the rejection
        var violations = TakeCspViolations();

        Assert.Equal("TypeError", outcome);
        Assert.Contains(violations, v => v.StartsWith("connect-src blocked http://localhost:8080"));
    }
}
