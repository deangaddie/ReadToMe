using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Read2Me.E2eTests.Infrastructure;

namespace Read2Me.E2eTests.Tests.Native;

/// <summary>
/// The live relay between two browsers on one host (native-web 22, from the Angular class of the
/// same name): a mutation made from one context reaches the other through its receipt within two
/// seconds, and a browser that loses the hub reconnects on its own and keeps receiving receipts.
/// The reader's node menu is native-web 23, so the split is sent from the second context's own
/// origin through the agent API; the reader on both sides reacts to the receipt alone. Runs in
/// Chromium and Firefox.
/// </summary>
[Collection(E2eCollection.Name)]
public class LiveUpdateTests(E2eAppFixture app, PlaywrightFixture pw) : E2eTestBase(app, pw)
{
    protected override WebApp WebApp => WebApp.Native;

    [Fact]
    public async Task A_split_in_one_browser_reaches_the_other_within_two_seconds()
    {
        var builder = await App.SeedProjectAsync("native-live-split", "Native Live Book", "A. Author");

        await GotoAppAsync("projects/native-live-split/book");
        await Expect(Page.Locator(".book__chapter")).ToHaveTextAsync(["ch1"]);

        // A second, independent browser context on the same host.
        await using var other = await Page.Context.Browser!.NewContextAsync(new() { BaseURL = App.BaseUrl });
        var otherPage = await other.NewPageAsync();
        await otherPage.GotoAsync(AppPath("projects/native-live-split/book?mode=speakers"));
        var paragraphs = otherPage.Locator(".r2m-paragraph");
        await Expect(paragraphs).ToHaveCountAsync(3);

        // The split the row menu will send (native-web 23/24), posted from the other context.
        var split = await otherPage.APIRequest.PostAsync($"{App.BaseUrl}/api/projects/native-live-split/commands", new()
        {
            DataObject = new { type = "SplitAtParagraph", paragraphId = builder.ParagraphId("p2"), newChapterTitle = "From elsewhere" },
        });
        Assert.True(split.Ok, await split.TextAsync());
        await Expect(otherPage.Locator(".book__chapter")).ToHaveTextAsync(["ch1", "From elsewhere"]);

        // The first browser did nothing and sees the new chapter from the receipt alone.
        await Expect(Page.Locator(".book__chapter")).ToHaveTextAsync(["ch1", "From elsewhere"], new() { Timeout = 2_000 });
        await Expect(Page.Locator(".r2m-toast--info")).ToHaveTextAsync("Book updated elsewhere");
    }

    [Fact]
    public async Task Losing_the_hub_reopens_a_fresh_socket_and_receipts_resume()
    {
        var builder = await App.SeedProjectAsync("native-reconnect", "Native Reconnect Book", "A. Author");

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

        await GotoAppAsync("projects/native-reconnect/book");
        await Expect(Page.Locator(".book__chapter")).ToHaveTextAsync(["ch1"]);
        var dot = Page.Locator(".shell__conn");
        await Expect(dot).ToHaveClassAsync(new Regex("shell__conn--connected"));
        Assert.Equal(1, await Page.EvaluateAsync<int>("() => window.__r2mSockets.length"));

        // The dot flips to reconnecting and back faster than an assertion can poll (the first retry
        // is immediate), so the proof of a reconnect is the fresh socket the client opens.
        var reopened = Page.WaitForWebSocketAsync(new() { Timeout = 20_000 });
        await Page.EvaluateAsync("() => window.__r2mSockets.at(-1).close()");
        Assert.Contains("/hubs/live", (await reopened).Url);
        await Expect(dot).ToHaveAttributeAsync("aria-label", "Live updates connected", new() { Timeout = 20_000 });
        Assert.Equal(2, await Page.EvaluateAsync<int>("() => window.__r2mSockets.length"));

        // The healed connection still carries the project's receipts.
        var rename = await Page.APIRequest.PostAsync($"{App.BaseUrl}/api/projects/native-reconnect/commands", new()
        {
            DataObject = new { type = "UpdateChapterTitle", chapterId = builder.ChapterId("ch1"), title = "After reconnect" },
        });
        Assert.True(rename.Ok, await rename.TextAsync());
        await Expect(Page.Locator(".book__chapter")).ToHaveTextAsync(["After reconnect"], new() { Timeout = 5_000 });
    }

}
