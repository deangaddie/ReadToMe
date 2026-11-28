using System.Text.RegularExpressions;
using Read2Me.E2eTests.Infrastructure;

namespace Read2Me.E2eTests.Tests.Web;

/// <summary>
/// Throwaway proof for #481: losing the hub is announced once, assertively, and its return once,
/// politely, while startup says nothing; the connection indicator is an image named by its state,
/// not a live region. Every <c>ariaNotify</c> call is recorded, then made for real.
/// </summary>
public class LiveAnnounceProofTests(E2eAppFixture app, PlaywrightFixture pw) : E2eTestBase(app, pw)
{
    private const string Recorder = """
        (() => {
          window.__calls = [];
          for (const proto of [Document.prototype, Element.prototype]) {
            const real = proto.ariaNotify;
            if (!real) continue;
            proto.ariaNotify = function (text, options) {
              window.__calls.push(`${text} | ${options?.priority}`);
              return real.call(this, text, options);
            };
          }
          const Native = window.WebSocket;
          window.__r2mSockets = [];
          window.WebSocket = new Proxy(Native, {
            construct(target, args) {
              const ws = new target(...args);
              window.__r2mSockets.push(ws);
              return ws;
            },
          });
        })();
        """;

    // Needs a real browser: ariaNotify, the real hub and the accessibility tree.
    [Fact]
    public async Task Losing_and_regaining_the_hub_is_announced_once_each()
    {
        await Page.AddInitScriptAsync(Recorder);
        await GotoAppAsync("projects");
        var shell = Page.Locator("app-root");
        await Expect(shell).ToHaveAttributeAsync("data-live-state", "connected");

        Assert.True(await Page.EvaluateAsync<bool>("() => 'ariaNotify' in Document.prototype"), "no Document.ariaNotify");
        Assert.Empty(await Page.EvaluateAsync<string[]>("() => window.__calls"));
        var indicator = Page.Locator(".shell__conn");
        Assert.Equal("- img \"Live updates connected\"", await indicator.AriaSnapshotAsync());
        Assert.Null(await indicator.GetAttributeAsync("aria-live"));

        var reopened = Page.WaitForWebSocketAsync(new() { Timeout = 20_000 });
        await Page.EvaluateAsync("() => window.__r2mSockets.at(-1).close()");
        Assert.Contains("/hubs/live", (await reopened).Url);
        await Expect(shell).ToHaveAttributeAsync("data-live-state", "connected", new() { Timeout = 20_000 });
        await Expect(indicator).ToHaveAttributeAsync("aria-label", "Live updates connected");

        var calls = await Page.EvaluateAsync<string[]>("() => window.__calls");
        Assert.Equal(new[] { "Live updates disconnected — retrying | high", "Live updates reconnected | normal" }, calls);
        Assert.Equal("- img \"Live updates connected\"", await indicator.AriaSnapshotAsync());
        Assert.Matches(new Regex("shell__conn--connected"), await indicator.GetAttributeAsync("class") ?? "");

        TestContext.Current.TestOutputHelper?.WriteLine(
            $"{Page.Context.Browser!.BrowserType.Name} {Page.Context.Browser.Version}: {string.Join(" ; ", calls)}");
    }
}
