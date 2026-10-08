using Microsoft.Playwright;
using Read2Me.E2eTests.Infrastructure;

namespace Read2Me.E2eTests.Tests.Native;

/// <summary>
/// The structure tree's per-node menu (native-web 23, spec §7 named risk 2): one shared popover
/// anchored to the trigger of each open. Its keys stay inside the menu rather than moving the
/// tree's roving focus, focus returns to the tree item after Escape and after choosing an entry,
/// and the anchor survives a live receipt re-rendering the tree under the open menu. Runs in
/// Chromium and Firefox.
/// </summary>
[Collection(E2eCollection.Name)]
public class NodeMenuTests(E2eAppFixture app, PlaywrightFixture pw) : E2eTestBase(app, pw)
{
    protected override WebApp WebApp => WebApp.Native;

    private const string FocusedNode = """
        () => document.activeElement?.closest('[role="treeitem"]')?.dataset.nodeId ?? null
        """;

    private const string FocusedEntry = """
        () => document.activeElement?.closest('[role="menuitem"]')?.dataset.entry ?? null
        """;

    [Fact]
    public async Task Menu_keys_stay_in_the_menu_and_focus_returns_to_the_tree_item()
    {
        var book = await App.SeedLongBookAsync("native-menu-keys", "Native Menu Keys", "A. Author", chapters: 3, paragraphs: 2);
        var first = book.ChapterId("Chapter 1").ToString();

        await GotoAppAsync("projects/native-menu-keys/book");
        await Expect(Page.Locator(".tree__title")).ToHaveTextAsync(["Chapter 1", "Chapter 2", "Chapter 3"]);

        // Once the reader has settled on the first chapter, Shift+F10 opens the focused node's menu
        // with its first entry focused.
        await Expect(Page.Locator("r2m-book-page")).ToHaveAttributeAsync("data-current-chapter", first);
        var node = Page.Locator($"[data-node-id='{first}']");
        await node.FocusAsync();
        await Expect(node).ToBeFocusedAsync();
        await Page.Keyboard.PressAsync("Shift+F10");
        var menu = Page.GetByRole(AriaRole.Menu, new() { Name = "Actions for Chapter 1" });
        await Expect(menu).ToBeVisibleAsync();
        // Read mode selects paragraphs, so the selection shortcuts lead (native-web 25).
        Assert.Equal("select-unprocessed", await Page.EvaluateAsync<string?>(FocusedEntry));

        // Arrows move through the entries, wrapping; the tree's tab stop does not move with them.
        await Page.Keyboard.PressAsync("ArrowDown");
        Assert.Equal("attribute-node", await Page.EvaluateAsync<string?>(FocusedEntry));
        await Page.Keyboard.PressAsync("ArrowDown");
        Assert.Equal("edit-title", await Page.EvaluateAsync<string?>(FocusedEntry));
        await Page.Keyboard.PressAsync("End");
        Assert.Equal("delete", await Page.EvaluateAsync<string?>(FocusedEntry));
        await Page.Keyboard.PressAsync("ArrowDown");
        Assert.Equal("select-unprocessed", await Page.EvaluateAsync<string?>(FocusedEntry));
        await Expect(node).ToHaveAttributeAsync("tabindex", "0");

        // Escape closes the menu and puts focus back on the tree item.
        await Page.Keyboard.PressAsync("Escape");
        await Expect(menu).ToBeHiddenAsync();
        Assert.Equal(first, await Page.EvaluateAsync<string?>(FocusedNode));

        // Choosing an entry (Delete, then Cancel in its confirm) also returns focus to the item.
        await Page.Keyboard.PressAsync("Shift+F10");
        await Expect(menu).ToBeVisibleAsync();
        await Page.Keyboard.PressAsync("End");
        await Page.Keyboard.PressAsync("Enter");
        var confirm = Page.GetByRole(AriaRole.Dialog);
        await Expect(confirm).ToBeVisibleAsync();
        await Expect(confirm).ToContainTextAsync("Delete “Chapter 1”?");
        await confirm.Locator(".r2m-confirm-dialog__cancel").ClickAsync();
        await Expect(confirm).ToBeHiddenAsync();
        await Expect(menu).ToBeHiddenAsync();
        Assert.Equal(first, await Page.EvaluateAsync<string?>(FocusedNode));
        await Expect(Page.Locator(".tree__title")).ToHaveCountAsync(3);
    }

    [Fact]
    public async Task The_anchor_survives_a_receipt_re_rendering_the_tree_under_the_open_menu()
    {
        var book = await App.SeedLongBookAsync("native-menu-anchor", "Native Menu Anchor", "A. Author", chapters: 3, paragraphs: 2);
        var second = book.ChapterId("Chapter 2").ToString();

        await GotoAppAsync("projects/native-menu-anchor/book");
        await Expect(Page.Locator(".tree__title")).ToHaveTextAsync(["Chapter 1", "Chapter 2", "Chapter 3"]);

        var node = Page.Locator($"[data-node-id='{second}']");
        await node.HoverAsync();
        var trigger = node.Locator(".r2m-node-menu__trigger");
        await trigger.ClickAsync();
        var menu = Page.GetByRole(AriaRole.Menu, new() { Name = "Actions for Chapter 2" });
        await Expect(menu).ToBeVisibleAsync();
        var anchor = (await trigger.BoundingBoxAsync())!;
        var before = (await menu.BoundingBoxAsync())!;
        AssertAnchored(anchor, before);

        // A rename from elsewhere reaches the reader as a receipt and re-renders the tree.
        var rename = await Page.APIRequest.PostAsync($"{App.BaseUrl}/api/projects/native-menu-anchor/commands", new()
        {
            DataObject = new { type = "UpdateChapterTitle", chapterId = book.ChapterId("Chapter 1"), title = "Renamed" },
        });
        Assert.True(rename.Ok, await rename.TextAsync());
        await Expect(Page.Locator(".tree__title").First).ToHaveTextAsync("Renamed");

        // The menu is still open, still on its trigger.
        await Expect(menu).ToBeVisibleAsync();
        var after = (await menu.BoundingBoxAsync())!;
        Assert.InRange(after.X, before.X - 1, before.X + 1);
        Assert.InRange(after.Y, before.Y - 1, before.Y + 1);
        AssertAnchored((await trigger.BoundingBoxAsync())!, after);

        // And it still belongs to its node: Escape returns focus there.
        await Page.Keyboard.PressAsync("Escape");
        await Expect(menu).ToBeHiddenAsync();
        Assert.Equal(second, await Page.EvaluateAsync<string?>(FocusedNode));
    }

    /// <summary>The panel sits just below the trigger (block-end) and overlaps it horizontally.</summary>
    private static void AssertAnchored(LocatorBoundingBoxResult anchor, LocatorBoundingBoxResult panel)
    {
        Assert.InRange(panel.Y - (anchor.Y + anchor.Height), 0, 12);
        Assert.True(panel.X < anchor.X + anchor.Width && panel.X + panel.Width > anchor.X,
            $"panel x {panel.X}..{panel.X + panel.Width} does not overlap trigger x {anchor.X}..{anchor.X + anchor.Width}");
    }
}
