using System.Text.RegularExpressions;
using Read2Me.E2eTests.Infrastructure;

namespace Read2Me.E2eTests.Tests.Web;

/// <summary>
/// The one live smoke test: the bundled SignalR client against the real hub. The browser loses its
/// hub socket and reopens a fresh one on its own; then a split posted to the agent API, as another
/// browser's would be, reaches the page through its receipt within two seconds. The receipt arriving
/// proves the healed connection re-joined the project's group (receipts only reach group members).
/// The reconnect, re-join and refetch logic is pinned over a fake socket in
/// <c>live-reconnect.spec.ts</c> and <c>live.service.spec.ts</c>. Runs in Chromium and Firefox.
/// </summary>
public class LiveUpdateTests(E2eAppFixture app, PlaywrightFixture pw) : E2eTestBase(app, pw)
{
    // Needs a real browser: it is the one test of the bundle's SignalR client against the real hub.
    [Fact]
    public async Task After_losing_the_hub_a_split_made_elsewhere_reaches_the_page_within_two_seconds()
    {
        var book = await App.SeedProjectAsync("native-live-split", "Native Live Book", "A. Author");

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

        // 1. Cut the hub socket. The dot flips to reconnecting and back faster than an assertion
        // can poll (the first retry is immediate), so the proof of a reconnect is the fresh socket
        // the client opens.
        var reopened = Page.WaitForWebSocketAsync(new() { Timeout = 20_000 });
        await Page.EvaluateAsync("() => window.__r2mSockets.at(-1).close()");
        Assert.Contains("/hubs/live", (await reopened).Url);
        await Expect(dot).ToHaveAttributeAsync("aria-label", "Live updates connected", new() { Timeout = 20_000 });
        Assert.Equal(2, await Page.EvaluateAsync<int>("() => window.__r2mSockets.length"));

        // The toast lives 3 s, so watch for it from before the split.
        var toast = Page.Locator(".r2m-toast--info", new() { HasText = "Book updated elsewhere" })
            .WaitForAsync(new() { Timeout = 30_000 });

        // 2. A split from elsewhere: the host commits it and sends the receipt to the group.
        var split = await Page.APIRequest.PostAsync($"{App.BaseUrl}/api/projects/native-live-split/commands", new()
        {
            DataObject = new { type = "SplitAtParagraph", paragraphId = book.ParagraphId("p2"), newChapterTitle = "From elsewhere" },
        });
        Assert.True(split.Ok, await split.TextAsync());

        // 3. The page did nothing and sees the new chapter from the receipt alone, over the socket
        // it reopened.
        await Expect(Page.Locator(".book__chapter")).ToHaveTextAsync(["ch1", "From elsewhere"], new() { Timeout = 2_000 });
        await toast;
    }
}
