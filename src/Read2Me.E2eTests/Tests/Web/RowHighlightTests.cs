using System.Text.RegularExpressions;
using Read2Me.E2eTests.Infrastructure;

namespace Read2Me.E2eTests.Tests.Web;

/// <summary>
/// The book page's row highlight: the paragraph the user is on is framed and the item within it
/// carries an accent bar, so each node-menu trigger reads as its row's. The highlight outlives the
/// pointer and focus leaving for the menu's popover in the top layer, while that menu is open.
/// </summary>
public class RowHighlightTests(E2eAppFixture app, PlaywrightFixture pw) : E2eTestBase(app, pw)
{
    private static readonly Regex Frame = new(@"0px 0px 0px 1px inset");
    private static readonly Regex AccentBar = new(@"3px 0px 0px 0px inset");

    [Fact]
    public async Task An_item_and_its_paragraph_stay_highlighted_while_the_item_menu_is_open()
    {
        var book = await App.SeedMisSplitParagraphProjectAsync("row-highlight", "Row Highlight", "A. Author");

        await GotoAppAsync("projects/row-highlight/book?mode=speakers");
        // Settled values only: mid-fade, a highlight that is leaving still matches.
        await Page.AddStyleTagAsync(new() { Content = "* { transition: none !important; }" });
        var paragraph = Page.Locator($"[data-paragraph-id='{book.ParagraphId("p2")}']");
        var item = paragraph.Locator($"[data-item-id='{book.ItemId("clean")}']");
        var sibling = paragraph.Locator($"[data-item-id='{book.ItemId("lead")}']");
        await Expect(item).ToBeVisibleAsync();
        await Expect(paragraph).ToHaveCSSAsync("box-shadow", "none");

        await item.HoverAsync();
        await Expect(paragraph).ToHaveCSSAsync("box-shadow", Frame);
        await Expect(item).ToHaveCSSAsync("box-shadow", AccentBar);
        await Expect(sibling).ToHaveCSSAsync("box-shadow", "none");

        await item.Locator(".r2m-item__menu button").ClickAsync();
        var menu = Page.Locator("#r2m-node-menu-panel");
        await Expect(menu).ToBeVisibleAsync();
        // Pointer and focus both leave the row for the popover: only the open menu holds it now.
        await menu.Locator("[data-entry]").Last.HoverAsync();
        await Page.Keyboard.PressAsync("ArrowDown");
        Assert.True(await Page.EvaluateAsync<bool>("() => !!document.activeElement?.closest('#r2m-node-menu-panel')"));

        await Expect(item).ToHaveCSSAsync("box-shadow", AccentBar);
        await Expect(paragraph).ToHaveCSSAsync("box-shadow", Frame);
        await Expect(item.Locator(".r2m-item__menu")).ToHaveCSSAsync("opacity", "1");

        await Page.Keyboard.PressAsync("Escape");
        await Expect(menu).ToBeHiddenAsync();
        // Focus came back to the item's trigger: a keyboard user is still on the item.
        await Page.Mouse.MoveAsync(0, 0);
        await Expect(item).ToHaveCSSAsync("box-shadow", AccentBar);

        await Page.EvaluateAsync("() => document.activeElement?.blur()");
        await Expect(item).ToHaveCSSAsync("box-shadow", "none");
        await Expect(paragraph).ToHaveCSSAsync("box-shadow", "none");
    }
}
