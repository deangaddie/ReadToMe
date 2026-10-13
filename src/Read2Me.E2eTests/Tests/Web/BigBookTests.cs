using System.Text.Json;
using Microsoft.Playwright;
using Read2Me.E2eTests.Infrastructure;

namespace Read2Me.E2eTests.Tests.Web;

/// <summary>
/// The native reader's real layout over big books (spec §8.2 "big-book tests"), which happy-dom
/// cannot measure. A deep link into a nested book scrolls the chapter header to the top within a
/// pixel and holds it there while the three chapters before it are prepended, and a row menu opened
/// deep in the list sits on its trigger; the top row of a long book holds within a pixel when the
/// viewport is resized narrow and wide. Runs in Chromium and Firefox.
/// </summary>
public class BigBookTests(E2eAppFixture app, PlaywrightFixture pw) : E2eTestBase(app, pw)
{
    private const double TolerancePx = 1.0;

    /// <summary>The rendered row's top edge relative to the list's, or null when it is not rendered.</summary>
    private const string RowTop = """
        (key) => {
          const list = document.querySelector('r2m-measured-list');
          if (!list) return null;
          const row = list.querySelector(`[data-row-key="${key}"]`);
          return row ? row.getBoundingClientRect().top - list.getBoundingClientRect().top : null;
        }
        """;

    /// <summary>
    /// The chapter ids the window holds, in reading order, read off the list's public <c>items</c>: only
    /// the rows near the viewport are in the DOM, so the window cannot be counted from rendered headers.
    /// </summary>
    private const string WindowChapters = """
        () => (document.querySelector('r2m-measured-list')?.items ?? [])
          .filter((r) => r.kind === 'chapter').map((r) => r.chapterId)
        """;

    /// <summary>The key of the row at the list's top edge (the row the list reports as current).</summary>
    private const string TopRowKey = """
        () => {
          const list = document.querySelector('r2m-measured-list');
          if (!list) return null;
          const top = list.getBoundingClientRect().top;
          for (const row of list.querySelectorAll('[data-row-key]')) {
            const r = row.getBoundingClientRect();
            if (r.top <= top + 1 && r.bottom > top + 1) return row.dataset.rowKey;
          }
          return null;
        }
        """;

    /// <summary>
    /// Resolves once the list has looked the same for five animation frames in a row: its scroll
    /// offset, item count, top row and that row's offset. Rows grow as they render and the row
    /// observer corrects the scroll on the next frame, so a read straight after a prepend, a scroll
    /// or a resize can catch the list between the two; five quiet frames are past the correction.
    /// </summary>
    private const string Settle = $$"""
        () => new Promise((resolve) => {
          const rowTop = {{RowTop}};
          const topRowKey = {{TopRowKey}};
          let last = null, same = 0, frames = 0;
          const tick = () => {
            const list = document.querySelector('r2m-measured-list');
            const key = topRowKey();
            const now = JSON.stringify([list?.scrollTop, list?.items?.length, key, key && rowTop(key)]);
            same = now === last ? same + 1 : 0;
            last = now;
            if (same >= 5 || ++frames > 600) resolve(same >= 5); else requestAnimationFrame(tick);
          };
          requestAnimationFrame(tick);
        })
        """;

    private async Task SettleAsync() =>
        Assert.True(await Page.EvaluateAsync<bool>(Settle), "the reader list never settled for five frames");

    private const string ScrollBy = """
        (px) => {
          const list = document.querySelector('r2m-measured-list');
          if (!list) return null;
          list.scrollTop = list.scrollTop + px;
          list.dispatchEvent(new Event('scroll'));
        }
        """;

    [Fact]
    public async Task Deep_link_holds_the_header_at_the_top_over_three_prepends_and_a_row_menu_opens_on_its_trigger()
    {
        var book = await App.SeedNestedBookAsync("native-big-nested", "Native Nested Book", "A. Author");

        // A deep link into the last chapter of volume one: the chapters after it load until the
        // header can sit at the top, and it lands within a pixel.
        var deep = book.ChapterId("vol1-part2-ch2");
        await GotoAppAsync($"projects/native-big-nested/book?chapter={deep}");
        var header = $"chapter:{deep}";
        // The landed offset is the one the wait saw: a read after it can catch rows that have grown
        // as they render, before the row observer puts the list back on the next frame.
        var landed = (await (await Page.WaitForFunctionAsync(
            $"(key) => {{ const t = ({RowTop})(key); return t !== null && Math.abs(t) <= {TolerancePx} ? {{ t }} : null; }}",
            header)).JsonValueAsync<JsonElement>()).GetProperty("t").GetDouble();
        await Expect(Page.Locator("r2m-book-page")).ToHaveAttributeAsync("data-current-chapter", deep.ToString());

        // The chapters are short, so the edge check prepends chapter 3, then 2, then 1, one after
        // the other: a rounding error that compounded per prepend would show here.
        var first = book.ChapterId("vol1-part1-ch1").ToString();
        await Page.WaitForFunctionAsync($"(id) => ({WindowChapters})()[0] === id", first);
        await SettleAsync();
        var afterPrepend = await Page.EvaluateAsync<double>(RowTop, header);
        Assert.InRange(afterPrepend, landed - TolerancePx, landed + TolerancePx);
        await Expect(Page.Locator("r2m-book-page")).ToHaveAttributeAsync("data-current-chapter", deep.ToString());

        // Below the anchored header the rendered window starts far down the list: a row menu opened
        // there sits on its trigger, not where the row would be without the prepends.
        var key = await Page.EvaluateAsync<string>("""
            () => {
              const list = document.querySelector('r2m-measured-list');
              const box = list.getBoundingClientRect();
              return [...list.querySelectorAll('[data-row-key^="paragraph:"]')]
                .find((row) => {
                  const r = row.getBoundingClientRect();
                  return r.top > box.top + 80 && r.bottom < box.bottom - 80;
                }).dataset.rowKey;
            }
            """);
        var row = Page.Locator($"[data-row-key='{key}'] .r2m-paragraph__menu");
        await row.HoverAsync();
        var trigger = row.Locator("button");
        await trigger.ClickAsync();
        var menu = Page.Locator("#r2m-node-menu-panel");
        await Expect(menu).ToBeVisibleAsync();
        NodeMenuTests.AssertAnchored((await trigger.BoundingBoxAsync())!, (await menu.BoundingBoxAsync())!);
    }

    [Fact]
    public async Task Anchoring_holds_over_a_narrow_and_a_wide_resize()
    {
        await App.SeedLongBookAsync("native-big-resize", "Native Resize Book", "A. Author");

        await GotoAppAsync("projects/native-big-resize/book");
        await Page.WaitForSelectorAsync(".r2m-paragraph", new() { State = WaitForSelectorState.Attached });
        await Page.EvaluateAsync(ScrollBy, 1500);
        await SettleAsync();
        var key = await Page.EvaluateAsync<string>(TopRowKey);
        Assert.NotNull(key);
        var offset = await Page.EvaluateAsync<double>(RowTop, key);

        // Narrow: every paragraph wraps to more lines, so every row above the top one grows.
        await Page.SetViewportSizeAsync(520, 700);
        await Page.WaitForFunctionAsync("() => window.innerWidth === 520");
        await SettleAsync();
        Assert.Equal(key, await Page.EvaluateAsync<string>(TopRowKey));
        var narrow = await Page.EvaluateAsync<double>(RowTop, key);
        Assert.InRange(narrow, offset - TolerancePx, offset + TolerancePx);

        // Wide again: rows shrink back, the top row still holds.
        await Page.SetViewportSizeAsync(1440, 900);
        await Page.WaitForFunctionAsync("() => window.innerWidth === 1440");
        await SettleAsync();
        Assert.Equal(key, await Page.EvaluateAsync<string>(TopRowKey));
        var wide = await Page.EvaluateAsync<double>(RowTop, key);
        Assert.InRange(wide, offset - TolerancePx, offset + TolerancePx);
    }
}
