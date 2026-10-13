using System.Text.RegularExpressions;
using Read2Me.E2eTests.Infrastructure;

namespace Read2Me.E2eTests.Tests.Web;

/// <summary>
/// The one live smoke test: the bundled SignalR client against the real hub, across two browser
/// contexts on one host. The first browser loses its hub socket and reopens a fresh one on its own;
/// then a split made from the second browser's row menu reaches the first through its receipt within
/// two seconds. The receipt arriving proves the healed connection re-joined the project's group
/// (receipts only reach group members). Runs in Chromium and Firefox.
/// </summary>
public class LiveUpdateTests(E2eAppFixture app, PlaywrightFixture pw) : E2eTestBase(app, pw)
{
    [Fact]
    public async Task After_losing_the_hub_a_split_in_one_browser_reaches_the_other_within_two_seconds()
    {
        await App.SeedProjectAsync("native-live-split", "Native Live Book", "A. Author");

        // Keep a handle on every WebSocket the page opens so the test can cut the hub's from
        // inside the page — to the client that is indistinguishable from the host going away.
        // (Playwright's WebSocket routing is not used: its .NET binding faults on the reconnect.)
        await Page.AddInitScriptAsync("""
            (() => {
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
            """);

        await GotoAppAsync("projects/native-live-split/book");
        await Expect(Page.Locator(".book__chapter")).ToHaveTextAsync(["ch1"]);
        var dot = Page.Locator(".shell__conn");
        await Expect(dot).ToHaveClassAsync(new Regex("shell__conn--connected"));
        Assert.Equal(1, await Page.EvaluateAsync<int>("() => window.__r2mSockets.length"));

        // 1. Cut the first browser's hub socket. The dot flips to reconnecting and back faster than
        // an assertion can poll (the first retry is immediate), so the proof of a reconnect is the
        // fresh socket the client opens.
        var reopened = Page.WaitForWebSocketAsync(new() { Timeout = 20_000 });
        await Page.EvaluateAsync("() => window.__r2mSockets.at(-1).close()");
        Assert.Contains("/hubs/live", (await reopened).Url);
        await Expect(dot).ToHaveAttributeAsync("aria-label", "Live updates connected", new() { Timeout = 20_000 });
        Assert.Equal(2, await Page.EvaluateAsync<int>("() => window.__r2mSockets.length"));

        // 2. A second, independent browser context on the same host.
        await using var other = await Page.Context.Browser!.NewContextAsync(new() { BaseURL = App.BaseUrl });
        var otherPage = await other.NewPageAsync();
        await otherPage.GotoAsync(AppPath("projects/native-live-split/book?mode=speakers"));
        var paragraphs = otherPage.Locator(".r2m-paragraph");
        await Expect(paragraphs).ToHaveCountAsync(3);

        // The first browser's toast lives 3 s and can come and go while the second is still being
        // checked, so watch for it from before the split.
        var toast = Page.Locator(".r2m-toast--info", new() { HasText = "Book updated elsewhere" })
            .WaitForAsync(new() { Timeout = 30_000 });

        // 3. The split, sent from the second context's row menu.
        var menu = paragraphs.Nth(1).Locator(".r2m-paragraph__menu");
        await menu.HoverAsync();
        await menu.Locator("button").ClickAsync();
        await otherPage.Locator("[role='menu'] [data-entry='split']").ClickAsync();
        var prompt = otherPage.Locator("r2m-text-prompt-dialog");
        await prompt.Locator(".r2m-text-prompt-dialog__input").FillAsync("From elsewhere");
        await prompt.Locator(".r2m-text-prompt-dialog__confirm").ClickAsync();
        await Expect(otherPage.Locator(".book__chapter")).ToHaveTextAsync(["ch1", "From elsewhere"]);

        // 4. The first browser did nothing and sees the new chapter from the receipt alone, over
        // the socket it reopened.
        await Expect(Page.Locator(".book__chapter")).ToHaveTextAsync(["ch1", "From elsewhere"], new() { Timeout = 2_000 });
        await toast;
    }
}
