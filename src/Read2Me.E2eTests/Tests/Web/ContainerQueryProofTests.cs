using Microsoft.Playwright;
using Read2Me.E2eTests.Infrastructure;

namespace Read2Me.E2eTests.Tests.Web;

/// <summary>Throwaway proof for #491: pages follow <c>.shell__main</c>'s width, not the viewport.</summary>
public class ContainerQueryProofTests(E2eAppFixture app, PlaywrightFixture pw) : E2eTestBase(app, pw)
{
    private async Task AssertNoSidewaysScrollAsync()
    {
        var widths = await Page.Locator(".shell__main").EvaluateAsync<int[]>(
            "el => [el.scrollWidth, el.clientWidth]");
        var (scroll, client) = (widths[0], widths[1]);
        Assert.True(scroll <= client, $".shell__main scrollWidth {scroll} > clientWidth {client}");
    }

    private Task<int> MainWidthAsync() =>
        Page.Locator(".shell__main").EvaluateAsync<int>("el => el.clientWidth");

    private Task OpenDrawerAsync() =>
        Page.GetByRole(AriaRole.Button, new() { Name = "Toggle activity drawer" }).ClickAsync();

    [Fact]
    public async Task Master_detail_stacks_with_panels_docked_and_sits_side_by_side_without()
    {
        await Page.SetViewportSizeAsync(1280, 800);
        await GotoAppAsync("settings/llm");
        var list = Page.Locator(".r2m-master-detail > .llm__list");
        var editor = Page.Locator(".r2m-master-detail > .llm__editor");
        await Expect(list).ToBeVisibleAsync();
        await Expect(Page.Locator(".shell__rail--expanded")).ToBeVisibleAsync();
        if (!await editor.IsVisibleAsync()) await list.Locator(".r2m-config-list__row").First.ClickAsync();
        await Expect(editor).ToBeVisibleAsync();

        await OpenDrawerAsync();
        await Expect(Page.Locator(".shell__drawer")).ToBeVisibleAsync();
        Console.WriteLine($"docked: .shell__main clientWidth {await MainWidthAsync()}");
        var l = (await list.BoundingBoxAsync())!;
        var e = (await editor.BoundingBoxAsync())!;
        Console.WriteLine($"docked: list left {l.X}, editor left {e.X}, editor top {e.Y}, list bottom {l.Y + l.Height}");
        Assert.Equal(l.X, e.X, 1.0);
        Assert.True(e.Y >= l.Y + l.Height - 1, "editor should follow the list");
        await AssertNoSidewaysScrollAsync();

        await OpenDrawerAsync();
        await Expect(Page.Locator(".shell__drawer")).ToHaveCountAsync(0);
        Console.WriteLine($"closed: .shell__main clientWidth {await MainWidthAsync()}");
        l = (await list.BoundingBoxAsync())!;
        e = (await editor.BoundingBoxAsync())!;
        Console.WriteLine($"closed: list left {l.X} width {l.Width}, editor left {e.X}");
        Assert.True(e.X >= l.X + l.Width - 1, "editor should sit right of the list");
        Assert.Equal(l.Y, e.Y, 1.0);
        await AssertNoSidewaysScrollAsync();
    }

    [Fact]
    public async Task Overview_drops_to_one_column_with_the_drawer_and_History_docked()
    {
        await App.SeedProjectAsync("cq-overview", "CQ Overview", "A. Author");
        await Page.SetViewportSizeAsync(1280, 800);
        await GotoAppAsync("projects/cq-overview");
        var stepper = Page.Locator(".r2m-pipeline");
        var details = Page.Locator("r2m-project-details-panel");
        await Expect(stepper).ToBeVisibleAsync();

        await OpenDrawerAsync();
        await Page.GetByRole(AriaRole.Button, new() { Name = "History", Exact = true }).ClickAsync();
        await Expect(Page.Locator(".shell__history")).ToBeVisibleAsync();
        await Expect(Page.Locator(".shell__drawer")).ToBeVisibleAsync();
        Console.WriteLine($"overview docked: .shell__main clientWidth {await MainWidthAsync()}");
        await Expect(details).ToBeVisibleAsync();
        var s = (await stepper.BoundingBoxAsync())!;
        var d = (await details.BoundingBoxAsync())!;
        Console.WriteLine($"overview docked: stepper left {s.X} bottom {s.Y + s.Height}, details left {d.X} top {d.Y}");
        Assert.Equal(s.X, d.X, 1.0);
        Assert.True(d.Y >= s.Y + s.Height - 1, "details should follow the stepper");
        await AssertNoSidewaysScrollAsync();
    }

    [Fact]
    public async Task The_pronunciation_pill_opens_next_to_its_selection_inside_the_viewport()
    {
        await App.SeedProjectAsync("cq-pill", "CQ Pill", "A. Author");
        await Page.SetViewportSizeAsync(1280, 800);
        await GotoAppAsync("projects/cq-pill/book?mode=speakers");
        var text = Page.Locator(".r2m-item__text", new() { HasText = "It was a dark and stormy night." });
        await Expect(text).ToBeVisibleAsync();
        await OpenDrawerAsync();
        await Expect(Page.Locator(".shell__drawer")).ToBeVisibleAsync();

        var word = await text.EvaluateAsync<double[]>("""
            el => {
              const walker = document.createTreeWalker(el, NodeFilter.SHOW_TEXT);
              let node;
              while ((node = walker.nextNode()) && !node.data.includes('stormy')) {}
              const start = node.data.indexOf('stormy');
              const range = document.createRange();
              range.setStart(node, start);
              range.setEnd(node, start + 'stormy'.length);
              const sel = document.getSelection();
              sel.removeAllRanges();
              sel.addRange(range);
              const r = range.getBoundingClientRect();
              return [r.left, r.top, r.width, r.height];
            }
            """);
        var pill = Page.Locator(".pron-pill__button");
        await Expect(pill).ToBeVisibleAsync();
        Assert.True(await Page.Locator(".pron-pill__popover").EvaluateAsync<bool>("el => el.matches(':popover-open')"));
        var p = (await pill.BoundingBoxAsync())!;
        Console.WriteLine($"word [{string.Join(", ", word)}], pill x {p.X} y {p.Y} w {p.Width} h {p.Height}");
        Assert.True(p.X >= 0 && p.Y >= 0 && p.X + p.Width <= 1280 && p.Y + p.Height <= 800, "pill inside the viewport");
        Assert.Equal(word[0] + word[2] / 2, p.X + p.Width / 2, 2.0);
        Assert.InRange(word[1] - (p.Y + p.Height), 0, 12);
    }
}
