using Microsoft.Playwright;
using Read2Me.E2eTests.Infrastructure;

namespace Read2Me.E2eTests.Tests.Web;

/// <summary>
/// The Book view's Pronunciations panel (pronunciation ticket 16) where only a browser can tell:
/// a wide page lays it out as a third column beside the tree and the reader, and a narrow one
/// floats it over the reader's right edge. Rows, the pill and the Activity swap are unit specs.
/// </summary>
public class PronunciationPanelTests(E2eAppFixture app, PlaywrightFixture pw) : E2eTestBase(app, pw)
{
    private async Task<(LocatorBoundingBoxResult Tree, LocatorBoundingBoxResult Reader, LocatorBoundingBoxResult Panel)>
        OpenPanelAsync(string folder)
    {
        await GotoAppAsync($"projects/{folder}/book?mode=speakers");
        await Expect(Page.Locator(".r2m-item__text").First).ToBeVisibleAsync();
        await Page.Locator("[data-action=toggle-pronunciations]").ClickAsync();
        var panel = Page.Locator("r2m-pronunciation-panel");
        await Expect(panel).ToBeVisibleAsync();
        await Expect(panel).ToContainTextAsync("Select a word in the book to add one.");
        return ((await Page.Locator(".book__tree").BoundingBoxAsync())!,
            (await Page.Locator(".book__reader").BoundingBoxAsync())!,
            (await panel.BoundingBoxAsync())!);
    }

    // Needs a real browser: the third column is real grid layout.
    [Fact]
    public async Task On_a_wide_page_the_panel_is_a_third_column_beside_the_tree_and_the_reader()
    {
        await App.SeedProjectAsync("pron-panel-wide", "Panel Book", "A. Author");
        await Page.SetViewportSizeAsync(1440, 900);

        var (tree, reader, panel) = await OpenPanelAsync("pron-panel-wide");

        Assert.True(tree.X + tree.Width <= reader.X, $"tree {tree.X}+{tree.Width} overlaps reader {reader.X}");
        Assert.True(reader.X + reader.Width <= panel.X, $"reader {reader.X}+{reader.Width} overlaps panel {panel.X}");
        Assert.InRange(panel.Width, 300, 340);
        Assert.True(reader.Width >= 400, $"reader squeezed to {reader.Width} px");
    }

    // Needs a real browser: floating over the reader's edge is real layout under a real breakpoint.
    [Fact]
    public async Task On_a_narrow_page_the_panel_floats_over_the_reader_s_right_edge()
    {
        await App.SeedProjectAsync("pron-panel-narrow", "Panel Book", "A. Author");
        await Page.SetViewportSizeAsync(1000, 800);

        var (_, reader, panel) = await OpenPanelAsync("pron-panel-narrow");

        // The reader keeps its width under the panel, which covers its right edge and is what the pointer finds there.
        Assert.True(panel.X < reader.X + reader.Width, $"panel {panel.X} beside reader ending {reader.X + reader.Width}");
        Assert.InRange(reader.X + reader.Width - (panel.X + panel.Width), -2, 2);
        var onTop = await Page.EvaluateAsync<bool>(
            "p => !!document.elementFromPoint(p.x, p.y)?.closest('r2m-pronunciation-panel')",
            new { x = (double)(panel.X + panel.Width / 2), y = (double)(panel.Y + 40) });
        Assert.True(onTop);
    }

    // Needs a real browser: a drag resizing the grid, and the panel floating beside the wider tree, are real layout.
    [Fact]
    public async Task Dragging_the_tree_wider_floats_the_panel_keeps_the_reader_readable_and_survives_a_reload()
    {
        await App.SeedProjectAsync("pron-panel-resize", "Panel Book", "A. Author");
        await Page.SetViewportSizeAsync(1280, 800);
        var (before, _, _) = await OpenPanelAsync("pron-panel-resize");

        var gutter = (await Page.Locator("r2m-splitter").BoundingBoxAsync())!;
        var (x, y) = (gutter.X + gutter.Width / 2, gutter.Y + gutter.Height / 2);
        await Page.Mouse.MoveAsync(x, y);
        await Page.Mouse.DownAsync();
        await Page.Mouse.MoveAsync(x + 150, y, new() { Steps = 5 });
        await Page.Mouse.MoveAsync(x + 400, y, new() { Steps = 5 });
        await Page.Mouse.UpAsync();

        var tree = Page.Locator(".book__tree");
        var panel = Page.Locator("r2m-pronunciation-panel");
        await Expect(panel).ToHaveCSSAsync("position", "absolute");
        var after = (await tree.BoundingBoxAsync())!;
        var reader = (await Page.Locator(".book__reader").BoundingBoxAsync())!;
        var floating = (await panel.BoundingBoxAsync())!;
        Assert.True(after.Width >= before.Width + 100, $"tree {before.Width} -> {after.Width} px");
        Assert.True(after.Width <= 481, $"tree past its 480 px cap: {after.Width} px");
        Assert.True(reader.Width >= 450, $"reader squeezed to {reader.Width} px");
        Assert.True(floating.X < reader.X + reader.Width, $"panel {floating.X} beside reader ending {reader.X + reader.Width}");

        await Page.ReloadAsync();
        await Expect(Page.Locator(".r2m-item__text").First).ToBeVisibleAsync();
        var reloaded = (await tree.BoundingBoxAsync())!;
        Assert.InRange(reloaded.Width, after.Width - 1, after.Width + 1);
    }
}
