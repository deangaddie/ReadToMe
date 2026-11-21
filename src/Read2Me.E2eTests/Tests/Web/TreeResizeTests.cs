using Read2Me.E2eTests.Infrastructure;

namespace Read2Me.E2eTests.Tests.Web;

/// <summary>
/// Dragging the Book view's structure tree wider (spec #309). The drag holds a pointer capture,
/// so the class runs alone (<see cref="PointerCaptureCollection"/>).
/// </summary>
[Collection(PointerCaptureCollection.Name)]
public class TreeResizeTests(E2eAppFixture app, PlaywrightFixture pw) : E2eTestBase(app, pw)
{
    // Needs a real browser: a drag resizing the grid, and the panel floating beside the wider tree, are real layout.
    [Fact]
    public async Task Dragging_the_tree_wider_floats_the_panel_keeps_the_reader_readable_and_survives_a_reload()
    {
        await App.SeedProjectAsync("pron-panel-resize", "Panel Book", "A. Author");
        await GotoAppAsync("projects/pron-panel-resize/book?mode=speakers");
        await Expect(Page.Locator(".r2m-item__text").First).ToBeVisibleAsync();
        await Page.Locator("[data-action=toggle-pronunciations]").ClickAsync();
        var tree = Page.Locator(".book__tree");
        var panel = Page.Locator("r2m-pronunciation-panel");
        await Expect(panel).ToContainTextAsync("Select a word in the book to add one.");
        // The default 1440 px page docks the panel beside the 280 px tree, so the drag is what floats it.
        await Expect(panel).Not.ToHaveCSSAsync("position", "absolute");
        var before = (await tree.BoundingBoxAsync())!;

        var gutter = (await Page.Locator("r2m-splitter").BoundingBoxAsync())!;
        var (x, y) = (gutter.X + gutter.Width / 2, gutter.Y + gutter.Height / 2);
        await Page.Mouse.MoveAsync(x, y);
        await Page.Mouse.DownAsync();
        await Page.Mouse.MoveAsync(x + 150, y, new() { Steps = 5 });
        await Page.Mouse.MoveAsync(x + 400, y, new() { Steps = 5 });
        await Page.Mouse.UpAsync();

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
