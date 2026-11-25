using Microsoft.Playwright;
using Read2Me.E2eTests.Infrastructure;

namespace Read2Me.E2eTests.Tests.Web;

/// <summary>Throwaway proof for #466: the shared tooltip can be hovered (WCAG 1.4.13).</summary>
public class TooltipHoverProofTests(E2eAppFixture app, PlaywrightFixture pw, ITestOutputHelper output) : E2eTestBase(app, pw)
{
    [Fact]
    public async Task A_gated_controls_tip_stays_open_while_hovered_and_closes_once_the_pointer_leaves()
    {
        const string folder = "tip-hover-proof";
        await App.SeedLongBookAsync(folder, "Tip Hover Proof", "A. Author", chapters: 3, paragraphs: 2);
        await GotoAppAsync($"projects/{folder}/book");
        // View a past step, so every gated control carries the read-only reason.
        await Page.EvaluateAsync($$"""
            async () => {
              await fetch('/api/projects/{{folder}}/history');
              await fetch('/api/projects/{{folder}}', {
                method: 'PATCH',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ title: 'Renamed' }),
              });
            }
            """);
        await Page.Locator(".shell__history-toggle").ClickAsync();
        var steps = Page.Locator(".history-sheet__step");
        await Expect(steps).ToHaveCountAsync(2);
        await steps.Last.ClickAsync();
        await Expect(Page.Locator(".shell__viewing")).ToContainTextAsync("Viewing #1");
        await Page.Locator(".shell__history-toggle").ClickAsync();

        const string reason = "Viewing a past step. Restore it to change anything";
        var gate = Page.Locator(".r2m-blockable[data-tooltip]:has([data-action=toggle-pronunciations])");
        await Expect(gate).ToHaveAttributeAsync("data-tooltip", reason);
        var tip = Page.Locator(".r2m-tooltip");

        var g = (await gate.BoundingBoxAsync())!;
        await Page.Mouse.MoveAsync(g.X + g.Width / 2, g.Y + g.Height / 2);
        await Expect(tip).ToBeVisibleAsync();
        await Expect(tip).ToHaveTextAsync(reason);

        // Cross the gap onto the tip in small steps, then rest there past the 200 ms hide delay.
        var t = (await tip.BoundingBoxAsync())!;
        output.WriteLine($"{Environment.GetEnvironmentVariable(PlaywrightFixture.BrowserVariable) ?? "chromium"}: gate {g.X},{g.Y} {g.Width}x{g.Height}; tip {t.X},{t.Y} {t.Width}x{t.Height}");
        Assert.True(t.Y >= g.Y + g.Height, "tip sits below the trigger (block-end)");
        await Page.Mouse.MoveAsync(t.X + t.Width / 2, t.Y + t.Height / 2, new() { Steps = 8 });
        await Page.WaitForTimeoutAsync(700);
        Assert.True(await tip.IsVisibleAsync(), "tip still open while hovered, past the hide delay");
        Assert.Equal("--r2m-tooltip", await gate.EvaluateAsync<string>("e => e.style.getPropertyValue('anchor-name')"));

        // Away from both, onto the app bar brand (no tip of its own): it closes after the delay.
        var away = (await Page.GetByRole(AriaRole.Link, new() { Name = "Read2Me" }).BoundingBoxAsync())!;
        await Page.Mouse.MoveAsync(away.X + away.Width / 2, away.Y + away.Height / 2, new() { Steps = 4 });
        Assert.True(await tip.IsVisibleAsync(), "still open for the hide delay");
        await Expect(tip).ToBeHiddenAsync();
        output.WriteLine($"{Environment.GetEnvironmentVariable(PlaywrightFixture.BrowserVariable) ?? "chromium"}: open while hovered past the delay, closed once the pointer left");
    }

    [Fact]
    public async Task A_tree_title_tip_and_the_next_row()
    {
        const string folder = "tip-tree-proof";
        var book = await App.SeedLongBookAsync(folder, "Tip Tree Proof", "A. Author", chapters: 3, paragraphs: 2);
        await GotoAppAsync($"projects/{folder}/book");
        await Expect(Page.Locator(".tree__title")).ToHaveCountAsync(3);
        var rename = await Page.APIRequest.PostAsync($"{App.BaseUrl}/api/projects/{folder}/commands", new()
        {
            DataObject = new
            {
                type = "UpdateChapterTitle", chapterId = book.ChapterId("Chapter 1"),
                title = "CHAPTER I. MR. SHERLOCK HOLMES, AND HOW HE CAME TO TAKE ROOMS IN BAKER STREET",
            },
        });
        Assert.True(rename.Ok, await rename.TextAsync());
        var first = Page.Locator(".tree__title").First;
        await Expect(first).ToHaveTextAsync(new System.Text.RegularExpressions.Regex("^CHAPTER I"));

        var tip = Page.Locator(".r2m-tooltip");
        var f = (await first.BoundingBoxAsync())!;
        await Page.Mouse.MoveAsync(f.X + 20, f.Y + f.Height / 2);
        await Expect(tip).ToBeVisibleAsync();
        var t = (await tip.BoundingBoxAsync())!;
        var secondRow = Page.Locator($"[data-node-id='{book.ChapterId("Chapter 2")}']");
        var s = (await secondRow.BoundingBoxAsync())!;
        output.WriteLine($"row1 title {f.Y}..{f.Y + f.Height}; tip {t.X},{t.Y} {t.Width}x{t.Height} (ends {t.Y + t.Height}); row2 {s.Y}..{s.Y + s.Height}");

        // From row 1, straight down through the tip (it covers row 2's top) onto row 2's middle.
        var x = t.X + t.Width / 2;
        Assert.True(x > s.X && x < s.X + s.Width, "the path is over row 2");
        await Page.Mouse.MoveAsync(x, f.Y + f.Height / 2);
        await Page.Mouse.MoveAsync(x, t.Y + t.Height / 2, new() { Steps = 4 });
        await Page.WaitForTimeoutAsync(500);
        Assert.True(await tip.IsVisibleAsync(), "held while the pointer is on it, over row 2");
        await Page.Mouse.MoveAsync(x, s.Y + s.Height / 2, new() { Steps = 4 });
        var hit = await Page.EvaluateAsync<string?>($"() => document.elementFromPoint({x}, {s.Y + s.Height / 2})?.closest('[data-node-id]')?.dataset.nodeId ?? null");
        Assert.Equal(book.ChapterId("Chapter 2").ToString(), hit);
        await Expect(tip).ToBeHiddenAsync();
        output.WriteLine($"{Environment.GetEnvironmentVariable(PlaywrightFixture.BrowserVariable) ?? "chromium"}: row 2's middle is below the tip and takes the hover; the tip closed");
    }
}
