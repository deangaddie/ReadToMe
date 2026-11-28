using Microsoft.Playwright;
using Read2Me.E2eTests.Infrastructure;

namespace Read2Me.E2eTests.Tests.Web;

/// <summary>Throwaway proof for #496: the speaker menu search row shows a focus ring, and a focused tree row keeps an outline under forced colours. Deleted in the PR's final commit.</summary>
public class FocusRingProofTests(E2eAppFixture app, PlaywrightFixture pw) : E2eTestBase(app, pw)
{
    private static bool IsFirefox => Environment.GetEnvironmentVariable("R2M_E2E_BROWSER") == "firefox";
    [Fact]
    public async Task Keyboard_opened_speaker_menu_rings_its_search_row()
    {
        var folder = $"ring-menu-{Guid.NewGuid():N}";
        var book = await App.SeedProjectAsync(folder, "Ring Menu", "Author");
        await GotoAppAsync($"projects/{folder}/book?mode=speakers");
        var item = Page.Locator($"[data-item-id='{book.ItemId("line1")}']");
        var chip = item.Locator(".r2m-speaker-chip__body");
        var menu = item.Locator(".r2m-speaker-chip__menu");

        await chip.FocusAsync();
        await Page.Keyboard.PressAsync("Enter");
        await Expect(menu).ToBeVisibleAsync();
        await Expect(menu.GetByLabel("Search characters", new() { Exact = true })).ToBeFocusedAsync();

        var ring = await menu.Locator(".r2m-speaker-menu__search").EvaluateAsync<string[]>("""
            e => { const s = getComputedStyle(e); return [s.outlineStyle, s.outlineWidth, s.outlineColor, s.boxShadow]; }
            """);
        Console.WriteLine($"[{(IsFirefox ? "firefox" : "chromium")}] search row outline: {string.Join(" | ", ring)}");
        Assert.True(ring[0] != "none" || ring[3] != "none", $"no ring: {string.Join(" | ", ring)}");
        Assert.Equal("solid", ring[0]);
        Assert.Equal("2px", ring[1]);
    }

    [Fact]
    public async Task Focused_tree_row_keeps_a_solid_outline_under_forced_colours()
    {
        if (!IsFirefox) await Page.EmulateMediaAsync(new() { ForcedColors = ForcedColors.Active });

        var book = await App.SeedLongBookAsync("ring-tree", "Ring Tree", "A. Author", chapters: 3, paragraphs: 2);
        await GotoAppAsync("projects/ring-tree/book");
        await Expect(Page.Locator(".tree__title")).ToHaveCountAsync(3);
        var node = Page.Locator($"[data-node-id='{book.ChapterId("Chapter 2")}']");
        await node.FocusAsync();
        await Page.Keyboard.PressAsync("ArrowDown");
        await Page.Keyboard.PressAsync("ArrowUp");

        var forced = await Page.EvaluateAsync<bool>("() => matchMedia('(forced-colors: active)').matches");
        var focused = Page.Locator("[role='treeitem']:focus-visible");
        await Expect(focused).ToHaveCountAsync(1);
        var outline = await focused.EvaluateAsync<string[]>("""
            e => { const s = getComputedStyle(e); return [s.outlineStyle, s.outlineWidth, s.outlineColor, s.boxShadow]; }
            """);
        Console.WriteLine($"[{(IsFirefox ? "firefox" : "chromium")}] forced-colors={forced} tree row outline: {string.Join(" | ", outline)}");
        Assert.Equal(!IsFirefox, forced);
        Assert.Equal("solid", outline[0]);
        Assert.NotEqual("0px", outline[1]);
    }
}
