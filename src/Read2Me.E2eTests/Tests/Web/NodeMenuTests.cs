using Microsoft.Playwright;
using Read2Me.E2eTests.Infrastructure;

namespace Read2Me.E2eTests.Tests.Web;

/// <summary>
/// The structure tree's per-node menu (spec §7 named risk 2): one shared popover in the real top
/// layer, anchored to the trigger of each open. The anchor survives a live receipt re-rendering the
/// tree under the open menu, and focus returns to the tree item after Escape and after choosing an
/// entry (Delete, then Cancel in the real confirm dialog). Runs in Chromium and Firefox.
/// </summary>
public class NodeMenuTests(E2eAppFixture app, PlaywrightFixture pw) : E2eTestBase(app, pw)
{
    private const string FocusedNode = """
        () => document.activeElement?.closest('[role="treeitem"]')?.dataset.nodeId ?? null
        """;

    private const string FocusedEntry = """
        () => document.activeElement?.closest('[role="menuitem"]')?.dataset.entry ?? null
        """;

    [Fact]
    public async Task The_anchor_survives_a_receipt_re_rendering_the_tree_and_focus_returns_to_the_tree_item()
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

        // And it still belongs to its node: Escape closes it and returns focus there.
        await Page.Keyboard.PressAsync("Escape");
        await Expect(menu).ToBeHiddenAsync();
        Assert.Equal(second, await Page.EvaluateAsync<string?>(FocusedNode));

        // Choosing an entry also returns focus to the item: Shift+F10 reopens the menu, End reaches
        // Delete, and Cancel in its confirm closes both.
        await Page.Keyboard.PressAsync("Shift+F10");
        await Expect(menu).ToBeVisibleAsync();
        await Page.Keyboard.PressAsync("End");
        Assert.Equal("delete", await Page.EvaluateAsync<string?>(FocusedEntry));
        await Page.Keyboard.PressAsync("Enter");
        var confirm = Page.GetByRole(AriaRole.Dialog);
        await Expect(confirm).ToBeVisibleAsync();
        await Expect(confirm).ToContainTextAsync("Delete “Chapter 2”?");
        await confirm.Locator(".r2m-confirm-dialog__cancel").ClickAsync();
        await Expect(confirm).ToBeHiddenAsync();
        await Expect(menu).ToBeHiddenAsync();
        Assert.Equal(second, await Page.EvaluateAsync<string?>(FocusedNode));
        await Expect(Page.Locator(".tree__title")).ToHaveCountAsync(3);
    }
}
