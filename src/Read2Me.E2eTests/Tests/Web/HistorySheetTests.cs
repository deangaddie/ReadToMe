using Microsoft.Playwright;
using Read2Me.E2eTests.Infrastructure;

namespace Read2Me.E2eTests.Tests.Web;

/// <summary>
/// The History sheet's layout (spec, "The History sheet"): docked beside the page, which stays in
/// view and narrows for it, and under the shell's 900 px breakpoint covering the page body with
/// the app bar still above it; and, viewing a step, the banner full width between the app bar and
/// a greyed page (a computed <c>saturate</c> filter) that is not inert. Layout only: the list, its
/// rows, viewing, the tabs and the tree browsing at a step, each page's read-only controls, Back to
/// current and live updates are spec-tested (shell.spec.ts, book-page.spec.ts).
/// </summary>
public class HistorySheetTests(E2eAppFixture app, PlaywrightFixture pw) : E2eTestBase(app, pw)
{
    // Needs a real browser: docking and covering are real layout under a real breakpoint.
    [Fact]
    public async Task The_sheet_docks_beside_the_page_and_covers_the_page_body_when_narrow()
    {
        await App.SeedProjectAsync("history-sheet", "History Sheet", "A. Author");
        await Page.SetViewportSizeAsync(1280, 800);
        await GotoAppAsync("projects/history-sheet");

        var main = Page.Locator(".shell__main");
        var sheet = Page.Locator(".shell__history");
        var mainBefore = (await main.BoundingBoxAsync())!;
        await Page.GetByRole(AriaRole.Button, new() { Name = "History" }).ClickAsync();
        await Expect(sheet.Locator(".history-sheet__row").First).ToBeVisibleAsync();

        var docked = (await sheet.BoundingBoxAsync())!;
        var mainBeside = (await main.BoundingBoxAsync())!;
        Assert.True(mainBeside.Width < mainBefore.Width - 300, "the page should narrow for the sheet");
        Assert.True(docked.X >= mainBeside.X + mainBeside.Width - 1, "the sheet should sit right of the page");
        Assert.True(docked.X + docked.Width >= 1279, "the sheet should reach the window's right edge");

        await Page.SetViewportSizeAsync(600, 800);
        var body = (await Page.Locator(".shell__body").BoundingBoxAsync())!;
        var appBar = (await Page.Locator(".shell__appbar").BoundingBoxAsync())!;
        await Expect(sheet).ToBeVisibleAsync();
        var covering = (await sheet.BoundingBoxAsync())!;
        Assert.Equal(body.X, covering.X, 1);
        Assert.Equal(body.Width, covering.Width, 1);
        Assert.Equal(body.Y, covering.Y, 1);
        Assert.True(covering.Y >= appBar.Y + appBar.Height - 1, "the app bar should stay above the sheet");
        // The sheet is on top: the point at the page body's centre belongs to it.
        Assert.True(await Page.EvaluateAsync<bool>(
            "() => !!document.elementFromPoint(300, 400)?.closest('.shell__history')"));
    }

    // Needs a real browser: the banner's place is real layout and the grey is a computed filter.
    [Fact]
    public async Task Viewing_a_step_puts_the_banner_between_the_app_bar_and_a_greyed_page()
    {
        await App.SeedProjectAsync("history-view", "History View", "A. Author");
        await Page.SetViewportSizeAsync(1280, 800);
        await GotoAppAsync("projects/history-view/book");
        // A second step to view: History starts on its first read, then a rename lands on top.
        await Page.EvaluateAsync("""
            async () => {
              await fetch('/api/projects/history-view/history');
              await fetch('/api/projects/history-view', {
                method: 'PATCH',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ title: 'Renamed' }),
              });
            }
            """);

        await Page.GetByRole(AriaRole.Button, new() { Name = "History" }).ClickAsync();
        var steps = Page.Locator(".history-sheet__step");
        await Expect(steps).ToHaveCountAsync(2);
        await steps.Last.ClickAsync();

        var banner = Page.Locator(".shell__viewing");
        await Expect(banner).ToContainTextAsync("Viewing #1");
        var main = Page.Locator(".shell__main");
        await Expect(main).Not.ToHaveAttributeAsync("inert", "");
        Assert.Contains("saturate", await main.EvaluateAsync<string>("el => getComputedStyle(el).filter"));
        // Under the app bar, or under the Setup check's start banner when this machine's check fails.
        var setupBanner = Page.Locator(".shell__setup-banner");
        var above = (await (await setupBanner.IsVisibleAsync() ? setupBanner : Page.Locator(".shell__appbar"))
            .BoundingBoxAsync())!;
        var shown = (await banner.BoundingBoxAsync())!;
        var body = (await Page.Locator(".shell__body").BoundingBoxAsync())!;
        Assert.Equal(above.Y + above.Height, shown.Y, 1);
        Assert.Equal(shown.Y + shown.Height, body.Y, 1);
        Assert.Equal(1280, shown.Width, 1);
    }
}
