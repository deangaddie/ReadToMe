using Microsoft.Playwright;
using Read2Me.E2eTests.Infrastructure;

namespace Read2Me.E2eTests.Tests.Web;

/// <summary>
/// Throwaway proof for #495: a label cut by an ellipsis shows its full text in the tooltip on
/// hover, on keyboard focus of its row, and on tap; a label that fits shows none.
/// </summary>
public class TruncatedTooltipProofTests(E2eAppFixture app, PlaywrightFixture pw) : E2eTestBase(app, pw)
{
    private readonly PlaywrightFixture _pw = pw;

    private const string LongName = "Don Quixote of La Mancha Knight of the Sorrowful Countenance";
    private const string LongFile = "Don Quixote of La Mancha - The Ingenious Gentleman, Complete Edition_20261128.m4b";

    private static ILocator OpenTip(IPage page) => page.Locator(".r2m-tooltip:popover-open");

    private async Task SeedAsync(string folder, string character)
    {
        await App.SeedProjectAsync(folder, "Don Quixote", "Miguel de Cervantes", character);
        var dir = Path.Combine(App.WorkspaceDir, folder, "output");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, LongFile), new byte[3]);
        File.WriteAllBytes(Path.Combine(dir, "Short.m4b"), new byte[3]);
    }

    /// <summary>The page has loaded its data, so nothing moves under the pointer.</summary>
    private static async Task SettleAsync(IPage page)
    {
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await page.WaitForTimeoutAsync(300);
    }

    private async Task ResetAsync()
    {
        await Page.Keyboard.PressAsync("Escape");
        await Page.Mouse.MoveAsync(1, 790);
        await Page.EvaluateAsync("() => document.activeElement?.blur()");
        await Expect(OpenTip(Page)).ToHaveCountAsync(0);
    }

    private static async Task AssertCutAsync(ILocator label) =>
        Assert.True(await label.EvaluateAsync<bool>("e => e.scrollWidth > e.clientWidth"), "label should be cut");

    /// <summary>Hover the label, then reach its row or control with the keyboard; each opens the full text.</summary>
    private async Task ProveHoverAndFocusAsync(ILocator label, ILocator focusable, string full)
    {
        await SettleAsync(Page);
        await AssertCutAsync(label);

        await label.HoverAsync();
        await Expect(OpenTip(Page)).ToHaveTextAsync(full);
        await ResetAsync();

        await focusable.FocusAsync();
        await Page.Keyboard.PressAsync("Shift+Tab");
        await Page.Keyboard.PressAsync("Tab");
        Assert.True(await focusable.EvaluateAsync<bool>("e => e === document.activeElement"));
        await Expect(OpenTip(Page)).ToHaveTextAsync(full);
        await ResetAsync();
    }

    [Fact]
    public async Task Cut_labels_show_their_full_text_on_hover_and_focus_and_fitting_ones_show_none()
    {
        Assert.Equal(60, LongName.Length);
        await SeedAsync("tip-long", LongName);
        await SeedAsync("tip-short", "Sancho");
        await Page.SetViewportSizeAsync(390, 800);

        // Cast list: a 60-character name; its row is the focusable.
        await GotoAppAsync("projects/tip-long/cast");
        var row = Page.Locator("a.cast__row").Filter(new() { HasText = "Don Quixote" });
        await ProveHoverAndFocusAsync(row.Locator(".cast__name"), row, LongName);

        // App bar: the next-step label, cut at phone width; the button is the focusable.
        var next = Page.Locator("button.shell__next-main");
        var nextLabel = (await next.Locator(".shell__next-label").TextContentAsync())!.Trim();
        await ProveHoverAndFocusAsync(next.Locator(".shell__next-label"), next, nextLabel);

        // Export: a long file name, reached by keyboard on its Download link.
        await GotoAppAsync("projects/tip-long/export");
        var output = Page.Locator($".export__output[data-output='{LongFile}']");
        await ProveHoverAndFocusAsync(
            output.Locator(".export__output-name"), output.Locator("[data-action='download-output']"), LongFile);

        // A name and a file name that fit show nothing, on hover or focus.
        await GotoAppAsync("projects/tip-short/cast");
        await SettleAsync(Page);
        var shortRow = Page.Locator("a.cast__row").Filter(new() { HasText = "Sancho" });
        await shortRow.Locator(".cast__name").HoverAsync();
        await Page.WaitForTimeoutAsync(800);
        await Expect(OpenTip(Page)).ToHaveCountAsync(0);
        await shortRow.FocusAsync();
        await Page.WaitForTimeoutAsync(800);
        await Expect(OpenTip(Page)).ToHaveCountAsync(0);

        await GotoAppAsync("projects/tip-short/export");
        await SettleAsync(Page);
        var shortOutput = Page.Locator(".export__output[data-output='Short.m4b']");
        await shortOutput.Locator(".export__output-name").HoverAsync();
        await Page.WaitForTimeoutAsync(800);
        await Expect(OpenTip(Page)).ToHaveCountAsync(0);
        await shortOutput.Locator("[data-action='download-output']").FocusAsync();
        await Page.WaitForTimeoutAsync(800);
        await Expect(OpenTip(Page)).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task The_speaker_menu_shows_a_cut_name_on_hover_and_on_the_arrow_key()
    {
        var book = await App.SeedProjectAsync("menu-long", "Don Quixote", "Miguel de Cervantes", LongName);
        await GotoAppAsync("projects/menu-long/book?mode=speakers");
        await SettleAsync(Page);
        var item = Page.Locator($"[data-item-id='{book.ItemId("line1")}']");
        await item.Locator("r2m-speaker-chip button[aria-haspopup='listbox']").ClickAsync();
        var menu = Page.Locator("r2m-speaker-menu");
        var name = menu.Locator(".r2m-speaker-menu__name").Filter(new() { HasText = "Don Quixote" });
        await AssertCutAsync(name);

        await name.HoverAsync();
        await Expect(OpenTip(Page)).ToHaveTextAsync(LongName);
        await Page.Mouse.MoveAsync(1, 790);
        await Expect(OpenTip(Page)).ToHaveCountAsync(0);

        // Focus stays in the search field; arrowing makes an option active.
        await menu.Locator(".r2m-speaker-menu__input").FocusAsync();
        for (var i = 0; i < 5 && !await menu.Locator(".r2m-speaker-menu__row--active").Filter(new() { HasText = "Don Quixote" }).IsVisibleAsync(); i++)
            await Page.Keyboard.PressAsync("ArrowDown");
        await Expect(OpenTip(Page)).ToHaveTextAsync(LongName);

        // Picking it closes the menu, and the tip with it.
        await Page.Keyboard.PressAsync("Enter");
        await Expect(menu).ToHaveCountAsync(0);
        await Page.WaitForTimeoutAsync(800);
        await Expect(OpenTip(Page)).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task A_tap_on_a_cut_label_shows_its_full_text()
    {
        await SeedAsync("tap-long", LongName);
        await GotoAppAsync("projects/tap-long/cast"); // stages the bundle

        await using var touch = await _pw.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            // List beside detail, so the tapped row stays on screen; its 360 px column cuts the name.
            ViewportSize = new ViewportSize { Width = 1280, Height = 800 },
            BaseURL = App.BaseUrl,
            HasTouch = true,
        });
        var page = await touch.NewPageAsync();

        await page.GotoAsync(AppPath("projects/tap-long/cast"));
        await SettleAsync(page);
        var name = page.Locator("a.cast__row .cast__name").Filter(new() { HasText = "Don Quixote" });
        await AssertCutAsync(name);
        // The tap also opens the character beside the list; the tip outlives focus moving there.
        await name.TapAsync();
        await page.WaitForURLAsync(new System.Text.RegularExpressions.Regex("/cast/."));
        await Expect(OpenTip(page)).ToHaveTextAsync(LongName);
        // The next tap elsewhere closes it.
        await page.Locator("h1, h2").First.TapAsync();
        await Expect(OpenTip(page)).ToHaveCountAsync(0);

        await page.SetViewportSizeAsync(390, 800);
        await page.GotoAsync(AppPath("projects/tap-long/export"));
        await SettleAsync(page);
        var file = page.Locator(".export__output-name").Filter(new() { HasText = "Ingenious" });
        await AssertCutAsync(file);
        await file.TapAsync();
        await Expect(OpenTip(page)).ToHaveTextAsync(LongFile);
    }
}
