using Microsoft.Playwright;
using Read2Me.E2eTests.Infrastructure;

namespace Read2Me.E2eTests.Tests.Native;

/// <summary>
/// The native reader over big books (native-web 22, spec §8.2 "big-book tests"): the deterministic
/// checks of the spike's scroll bench as tests, against two seeded books — one with many long
/// chapters, one with nested volumes and parts. A deep link scrolls the chapter header to the top
/// within a pixel; the top row holds within a pixel when the chapter before it is prepended and
/// when the viewport is resized narrow and wide; the window loads chapters on scroll and never
/// holds more than five; and the current chapter follows the row at the top. The tree-key tests
/// join this class with the structure tree (native-web 23). Runs in Chromium and Firefox.
/// </summary>
[Collection(E2eCollection.Name)]
public class BigBookTests(E2eAppFixture app, PlaywrightFixture pw) : E2eTestBase(app, pw)
{
    private const double TolerancePx = 1.0;

    protected override WebApp WebApp => WebApp.Native;

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

    private const string ScrollBy = """
        (px) => {
          const list = document.querySelector('r2m-measured-list');
          if (!list) return null;
          list.scrollTop = list.scrollTop + px;
          list.dispatchEvent(new Event('scroll'));
        }
        """;

    [Fact]
    public async Task Deep_link_scrolls_the_chapter_header_to_the_top_and_holds_it_over_the_prepend()
    {
        var book = await App.SeedLongBookAsync("native-big-long", "Native Long Book", "A. Author");
        var target = book.ChapterId("Chapter 6");
        var before = book.ChapterId("Chapter 5");

        await GotoAppAsync($"projects/native-big-long/book?chapter={target}");
        var header = $"chapter:{target}";
        await Page.WaitForSelectorAsync($"[data-row-key='{header}']", new() { State = WaitForSelectorState.Attached });

        // scrollToIndex lands within a pixel, and the current chapter is the one at the top.
        await Page.WaitForFunctionAsync($"(key) => {{ const t = ({RowTop})(key); return t !== null && Math.abs(t) <= {TolerancePx}; }}", header);
        var landed = await Page.EvaluateAsync<double>(RowTop, header);
        Assert.InRange(landed, -TolerancePx, TolerancePx);
        await Expect(Page.Locator("r2m-book-page")).ToHaveAttributeAsync("data-current-chapter", target.ToString());

        // The edge check prepends the chapter before: the anchored header does not move.
        await Page.WaitForFunctionAsync($"(id) => ({WindowChapters})().includes(id)", before.ToString());
        await Page.WaitForTimeoutAsync(500);
        var afterPrepend = await Page.EvaluateAsync<double>(RowTop, header);
        Assert.InRange(afterPrepend, landed - TolerancePx, landed + TolerancePx);
        Assert.Equal((await Page.EvaluateAsync<string[]>(WindowChapters))[0], before.ToString());
        await Expect(Page.Locator("r2m-book-page")).ToHaveAttributeAsync("data-current-chapter", target.ToString());
    }

    [Fact]
    public async Task Anchoring_holds_over_a_narrow_and_a_wide_resize()
    {
        await App.SeedLongBookAsync("native-big-resize", "Native Resize Book", "A. Author");

        await GotoAppAsync("projects/native-big-resize/book");
        await Page.WaitForSelectorAsync(".r2m-paragraph", new() { State = WaitForSelectorState.Attached });
        await Page.EvaluateAsync(ScrollBy, 1500);
        await Page.WaitForTimeoutAsync(400);
        var key = await Page.EvaluateAsync<string>(TopRowKey);
        Assert.NotNull(key);
        var offset = await Page.EvaluateAsync<double>(RowTop, key);

        // Narrow: every paragraph wraps to more lines, so every row above the top one grows.
        await Page.SetViewportSizeAsync(520, 700);
        await Page.WaitForTimeoutAsync(600);
        Assert.Equal(key, await Page.EvaluateAsync<string>(TopRowKey));
        var narrow = await Page.EvaluateAsync<double>(RowTop, key);
        Assert.InRange(narrow, offset - TolerancePx, offset + TolerancePx);

        // Wide again: rows shrink back, the top row still holds.
        await Page.SetViewportSizeAsync(1440, 900);
        await Page.WaitForTimeoutAsync(600);
        Assert.Equal(key, await Page.EvaluateAsync<string>(TopRowKey));
        var wide = await Page.EvaluateAsync<double>(RowTop, key);
        Assert.InRange(wide, offset - TolerancePx, offset + TolerancePx);
    }

    [Fact]
    public async Task Window_loads_chapters_on_scroll_caps_at_five_and_the_current_chapter_follows_the_top_row()
    {
        var book = await App.SeedLongBookAsync("native-big-window", "Native Window Book", "A. Author");

        await GotoAppAsync("projects/native-big-window/book");
        await Page.WaitForSelectorAsync(".r2m-paragraph", new() { State = WaitForSelectorState.Attached });
        var first = book.ChapterId("Chapter 1").ToString();
        var last = book.ChapterId("Chapter 12").ToString();
        await Expect(Page.Locator("r2m-book-page")).ToHaveAttributeAsync("data-current-chapter", first);

        // Page through the whole book; the window never holds more than five chapters.
        var max = 0;
        for (var i = 0; i < 80; i++)
        {
            await Page.EvaluateAsync(ScrollBy, 900);
            await Page.WaitForTimeoutAsync(150);
            var window = await Page.EvaluateAsync<string[]>(WindowChapters);
            max = Math.Max(max, window.Length);
            Assert.True(window.Length <= 5, $"window held {window.Length} chapters: {string.Join(", ", window)}");
            if (window[^1] == last) break;
        }
        Assert.Equal(5, max);
        var final = await Page.EvaluateAsync<string[]>(WindowChapters);
        Assert.Equal(last, final[^1]);

        // The current chapter is the chapter of the row at the top edge.
        await Page.WaitForTimeoutAsync(300);
        var topKey = await Page.EvaluateAsync<string>(TopRowKey);
        var topChapter = await Page.EvaluateAsync<string>(
            "(key) => document.querySelector('r2m-measured-list').items.find((r) => r.key === key)?.chapterId", topKey);
        await Expect(Page.Locator("r2m-book-page")).ToHaveAttributeAsync("data-current-chapter", topChapter);
    }

    [Fact]
    public async Task Nested_book_fills_its_window_across_parts_and_volumes_and_follows_the_top_row()
    {
        var book = await App.SeedNestedBookAsync("native-big-nested", "Native Nested Book", "A. Author");

        // Chapters are a few lines each, so the window grows across part and volume ends until a
        // screen of content sits below the viewport (a full window drops nothing still on screen).
        await GotoAppAsync("projects/native-big-nested/book");
        await Page.WaitForSelectorAsync(".r2m-paragraph", new() { State = WaitForSelectorState.Attached });
        var fifth = book.ChapterId("vol2-part1-ch1").ToString();
        await Page.WaitForFunctionAsync($"(id) => ({WindowChapters})().includes(id)", fifth);
        var window = await Page.EvaluateAsync<string[]>(WindowChapters);
        Assert.Equal(
            [
                book.ChapterId("vol1-part1-ch1").ToString(),
                book.ChapterId("vol1-part1-ch2").ToString(),
                book.ChapterId("vol1-part2-ch1").ToString(),
                book.ChapterId("vol1-part2-ch2").ToString(),
                fifth,
            ],
            window.Take(5));

        // Scrolling the second chapter's header to the top makes it the current chapter. (A header
        // further down cannot reach the top until enough content sits below it; that is the deep
        // link's job, below.)
        var second = book.ChapterId("vol1-part1-ch2");
        await Page.EvaluateAsync("""
            (id) => {
              const list = document.querySelector('r2m-measured-list');
              const index = list.items.findIndex((r) => r.key === `chapter:${id}`);
              list.scrollToIndex(index);
            }
            """, second.ToString());
        await Expect(Page.Locator("r2m-book-page")).ToHaveAttributeAsync("data-current-chapter", second.ToString());
        var top = await Page.EvaluateAsync<double>(RowTop, $"chapter:{second}");
        Assert.InRange(top, -TolerancePx, TolerancePx);

        // A deep link into the last chapter of volume one opens there: the chapters after it load
        // until the header can sit at the top, and the chapter before it is prepended without
        // moving the header.
        var deep = book.ChapterId("vol1-part2-ch2");
        await Page.GotoAsync(AppPath($"projects/native-big-nested/book?chapter={deep}"));
        var header = $"chapter:{deep}";
        await Page.WaitForFunctionAsync($"(key) => {{ const t = ({RowTop})(key); return t !== null && Math.abs(t) <= {TolerancePx}; }}", header);
        // Short chapters: the reader prepends chapter 3, then 2, then 1, one after the other, so a
        // rounding error that compounded per prepend would show here.
        var first = book.ChapterId("vol1-part1-ch1").ToString();
        await Page.WaitForFunctionAsync($"(id) => ({WindowChapters})()[0] === id", first);
        await Page.WaitForTimeoutAsync(500);
        var afterPrepend = await Page.EvaluateAsync<double>(RowTop, header);
        Assert.InRange(afterPrepend, -TolerancePx, TolerancePx);
        await Expect(Page.Locator("r2m-book-page")).ToHaveAttributeAsync("data-current-chapter", deep.ToString());
    }
}
