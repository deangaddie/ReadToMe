using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Read2Me.E2eTests.Infrastructure;

namespace Read2Me.E2eTests.Tests.Web;

/// <summary>
/// The live relay between two browsers on one host (Angular tickets 06 and 07): a mutation made in
/// one context reaches the other through its receipt within two seconds, and a browser that loses
/// the hub reconnects on its own and keeps receiving receipts.
/// </summary>
[Collection(E2eCollection.Name)]
public class LiveUpdateTests(E2eAppFixture app, PlaywrightFixture pw) : WebE2eTestBase(app, pw)
{
    [Fact]
    public async Task A_split_in_one_browser_reaches_the_other_within_two_seconds()
    {
        await App.SeedProjectAsync("web-live", "Web Live Book", "A. Author");

        await GotoAppAsync("/app/projects/web-live/book");
        await Expect(Page.Locator(".tree__title")).ToHaveTextAsync(["ch1"]);

        // A second, independent browser context on the same host.
        await using var other = await Page.Context.Browser!.NewContextAsync(new() { BaseURL = App.BaseUrl });
        var otherPage = await other.NewPageAsync();
        await otherPage.GotoAsync("/app/projects/web-live/book?mode=speakers");
        var paragraphs = otherPage.Locator("r2m-paragraph");
        await Expect(paragraphs).ToHaveCountAsync(3);

        var menu = paragraphs.Nth(1).Locator(".r2m-paragraph__menu");
        await menu.HoverAsync();
        await menu.Locator("button").ClickAsync();
        await otherPage.Locator(".mat-mdc-menu-panel [data-entry='split']").ClickAsync();
        var prompt = otherPage.Locator("r2m-text-prompt-dialog");
        await prompt.Locator(".r2m-text-prompt-dialog__input").FillAsync("From elsewhere");
        await prompt.Locator(".r2m-text-prompt-dialog__confirm").ClickAsync();
        await Expect(otherPage.Locator(".tree__title")).ToHaveTextAsync(["ch1", "From elsewhere"]);

        // The first browser did nothing and sees the new chapter from the receipt alone.
        await Expect(Page.Locator(".tree__title")).ToHaveTextAsync(["ch1", "From elsewhere"], new() { Timeout = 2_000 });
        await Expect(Page.Locator(".book__chapter")).ToHaveTextAsync(["ch1", "From elsewhere"], new() { Timeout = 2_000 });
    }

    [Fact]
    public async Task Losing_the_hub_reopens_a_fresh_socket_and_receipts_resume()
    {
        var builder = await App.SeedProjectAsync("web-reconnect", "Web Reconnect Book", "A. Author");

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

        await GotoAppAsync("/app/projects/web-reconnect/book");
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
        var rename = await Page.APIRequest.PostAsync($"{App.BaseUrl}/api/projects/web-reconnect/commands", new()
        {
            DataObject = new { type = "UpdateChapterTitle", chapterId = builder.ChapterId("ch1"), title = "After reconnect" },
        });
        Assert.True(rename.Ok, await rename.TextAsync());
        await Expect(Page.Locator(".tree__title")).ToHaveTextAsync(["After reconnect"], new() { Timeout = 5_000 });
    }
}
