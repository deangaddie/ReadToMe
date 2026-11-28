using Microsoft.Playwright;
using Read2Me.E2eTests.Infrastructure;

namespace Read2Me.E2eTests.Tests.Web;

/// <summary>Throwaway proof for #493: small targets get a 24 px hit area that overlaps no neighbour.</summary>
public class HitAreaProofTests(E2eAppFixture app, PlaywrightFixture pw, ITestOutputHelper output)
    : E2eTestBase(app, pw)
{
    // Every clickable thing on the page, each with its effective hit rect (its box, grown by an
    // .r2m-hit ::after), and any pair whose hit rects share area (one inside the other's ancestry aside).
    private void Log(string line)
    {
        output.WriteLine(line);
        var file = Environment.GetEnvironmentVariable("R2M_PROOF_LOG");
        if (file is not null) File.AppendAllText(file, line + Environment.NewLine);
    }

    private const string Overlaps = """
        () => {
          const sel = 'button, a[href], input, select, textarea, label.r2m-hit, [role=treeitem] .tree__title';
          const all = [...document.querySelectorAll(sel)].filter((el) => {
            const r = el.getBoundingClientRect();
            return r.width > 0 && r.height > 0 && !el.closest('[popover]:not(:popover-open)');
          });
          // ::after insets resolve against the padding box, inside any border.
          const hitRect = (el) => {
            const r = el.getBoundingClientRect();
            if (!el.classList.contains('r2m-hit')) return r;
            const s = getComputedStyle(el, '::after'), px = parseFloat;
            return new DOMRect(r.left + el.clientLeft + px(s.left), r.top + el.clientTop + px(s.top),
              el.clientWidth - px(s.left) - px(s.right), el.clientHeight - px(s.top) - px(s.bottom));
          };
          const label = (el) => el.getAttribute('aria-label') || el.className || el.tagName;
          const out = [];
          for (let i = 0; i < all.length; i++)
            for (let j = i + 1; j < all.length; j++) {
              const a = all[i], b = all[j];
              if (a.contains(b) || b.contains(a)) continue;
              // A label's own checkbox, and a label's control: the same target.
              if (a.control === b || b.control === a) continue;
              // The tree title is the row's own target, which its checkbox sits in (see structure-tree.css).
              if ([a, b].some((e) => e.matches('.tree__title')) && [a, b].some((e) => e.matches('.tree__hit, .tree__select'))) continue;
              const ra = hitRect(a), rb = hitRect(b);
              const w = Math.min(ra.right, rb.right) - Math.max(ra.left, rb.left);
              const h = Math.min(ra.bottom, rb.bottom) - Math.max(ra.top, rb.top);
              if (w > 0.01 && h > 0.01 && (a.matches('.r2m-hit') || b.matches('.r2m-hit')))
                out.push(`${label(a)} × ${label(b)} (${w.toFixed(1)}×${h.toFixed(1)})`);
            }
          return out;
        }
        """;

    // [box w, box h, area w, area h, reach left, top, right, bottom], against the border box.
    private const string HitSize = """
        (el) => {
          const r = el.getBoundingClientRect(), s = getComputedStyle(el, '::after'), px = parseFloat;
          const left = r.left + el.clientLeft + px(s.left), top = r.top + el.clientTop + px(s.top);
          const w = el.clientWidth - px(s.left) - px(s.right), h = el.clientHeight - px(s.top) - px(s.bottom);
          return [r.width, r.height, w, h, r.left - left, r.top - top, left + w - r.right, top + h - r.bottom];
        }
        """;

    private async Task<float[]> BoxAsync(ILocator el)
    {
        var b = (await el.BoundingBoxAsync())!;
        return [b.X, b.Y, b.Width, b.Height];
    }

    /// <summary>Clicks just inside each edge of the 24 px area and asserts each click toggled the box.</summary>
    private async Task ClickEdgesAsync(string name, ILocator host, ILocator box)
    {
        var size = await host.EvaluateAsync<double[]>(HitSize);
        Log($"{name}: box {size[0]}×{size[1]}, hit area {size[2]}×{size[3]} (insets L{size[4]} T{size[5]} R{size[6]} B{size[7]})");
        Assert.True(size[2] >= 24 && size[3] >= 24, $"{name} hit area is {size[2]}×{size[3]}");
        var b = await BoxAsync(box);
        float cx = b[0] + b[2] / 2, cy = b[1] + b[3] / 2;
        var points = new (string side, float x, float y)[]
        {
            ("left", b[0] - (float)size[4] + 1f, cy),
            ("top", cx, b[1] - (float)size[5] + 1f),
            ("right", b[0] + b[2] + (float)size[6] - 1f, cy),
            ("bottom", cx, b[1] + b[3] + (float)size[7] - 1f),
        };
        foreach (var (side, x, y) in points)
        {
            var before = await box.IsCheckedAsync();
            await Page.Mouse.ClickAsync(x, y);
            if (before) await Expect(box).Not.ToBeCheckedAsync();
            else await Expect(box).ToBeCheckedAsync();
            // The tree rolls a tick up into the selection after a fetch: let it settle before the next.
            await Page.WaitForTimeoutAsync(300);
            await Expect(box).ToBeCheckedAsync(new() { Checked = !before });
            Log($"  {side} edge ({x - b[0]:0.0}, {y - b[1]:0.0} from the box) toggled it");
        }
        if (await box.IsCheckedAsync()) await box.ClickAsync();
    }

    private async Task AssertNoOverlapAsync(string where)
    {
        var overlaps = await Page.EvaluateAsync<string[]>(Overlaps);
        Log($"{where}: {overlaps.Length} overlapping hit areas{(overlaps.Length > 0 ? ": " + string.Join("; ", overlaps) : "")}");
        Assert.Empty(overlaps);
    }

    // Needs a real browser: hit testing and layout.
    [Fact]
    public async Task Small_targets_take_clicks_across_24px_and_overlap_nothing()
    {
        var book = await App.SeedMisSplitParagraphProjectAsync("hit-area", "Hit Area", "A. Author");

        // Read mode: the paragraph checkbox, its compact speaker chip and the tree's checkboxes.
        await GotoAppAsync("projects/hit-area/book?mode=read");
        await Page.AddStyleTagAsync(new() { Content = "* { transition: none !important; }" });
        var paragraph = Page.Locator($"[data-paragraph-id='{book.ParagraphId("p2")}']");
        var narration = Page.Locator($"[data-paragraph-id='{book.ParagraphId("p1")}']");
        await Expect(paragraph.Locator(".r2m-paragraph__select")).ToBeVisibleAsync();
        Log($"paragraph heights: p1 {(await narration.BoundingBoxAsync())!.Height}, p2 {(await paragraph.BoundingBoxAsync())!.Height}");

        await ClickEdgesAsync("reader checkbox", paragraph.Locator("label.r2m-hit"), paragraph.Locator(".r2m-paragraph__select"));
        var treeBox = Page.Locator("r2m-structure-tree .tree__select").First;
        await ClickEdgesAsync("tree checkbox", Page.Locator("r2m-structure-tree label.r2m-hit").First, treeBox);
        await AssertNoOverlapAsync("read mode");

        var chip = paragraph.Locator("button.r2m-speaker-chip__body").First;
        var chipSize = await chip.EvaluateAsync<double[]>(HitSize);
        Log($"compact chip: box {chipSize[0]}×{chipSize[1]}, hit area {chipSize[2]}×{chipSize[3]} (reach above {chipSize[5]}, below {chipSize[7]})");
        Assert.True(chipSize[3] >= 24);
        var c = await BoxAsync(chip);
        // The outermost whole pixel inside the area: Firefox truncates mouse coordinates to whole pixels.
        var above = MathF.Ceiling(c[1] - (float)chipSize[5]);
        await Page.Mouse.ClickAsync(c[0] + c[2] / 2, above);
        await Expect(paragraph.Locator(".r2m-speaker-chip__menu")).ToBeVisibleAsync();
        Log($"  a click {c[1] - above:0.00} px above its visible edge opened its menu");
        await Page.Keyboard.PressAsync("Escape");
        await Expect(paragraph.Locator(".r2m-speaker-chip__menu")).ToBeHiddenAsync();
        var below = MathF.Floor(c[1] + c[3] + (float)chipSize[7] - 0.01f);
        await Page.Mouse.ClickAsync(c[0] + c[2] / 2, below);
        await Expect(paragraph.Locator(".r2m-speaker-chip__menu")).ToBeVisibleAsync();
        Log($"  a click {below - c[1] - c[3]:0.00} px below its visible edge opened its menu");
        await Page.Keyboard.PressAsync("Escape");

        // Speakers mode: three items stacked in p2, each with a compact chip.
        await GotoAppAsync("projects/hit-area/book?mode=speakers");
        await Expect(paragraph.Locator($"[data-item-id='{book.ItemId("clean")}'] button.r2m-speaker-chip__body")).ToBeVisibleAsync();
        await AssertNoOverlapAsync("speakers mode");

        // Audio mode: the item checkboxes, stacked.
        await GotoAppAsync("projects/hit-area/book?mode=audio");
        var lead = paragraph.Locator($"[data-item-id='{book.ItemId("lead")}']");
        await Expect(lead.Locator(".r2m-item__select")).ToBeVisibleAsync();
        Log($"paragraph heights (audio): p1 {(await narration.BoundingBoxAsync())!.Height}, p2 {(await paragraph.BoundingBoxAsync())!.Height}");
        await ClickEdgesAsync("item checkbox", lead.Locator("label.r2m-hit"), lead.Locator(".r2m-item__select"));
        await AssertNoOverlapAsync("audio mode");
    }

    // Needs a real browser: layout. Run on main and on the branch, and compare.
    [Fact]
    public async Task Row_heights()
    {
        var book = await App.SeedMisSplitParagraphProjectAsync("hit-heights", "Hit Heights", "A. Author");
        foreach (var mode in new[] { "read", "speakers", "audio" })
        {
            await GotoAppAsync($"projects/hit-heights/book?mode={mode}");
            var p2 = Page.Locator($"[data-paragraph-id='{book.ParagraphId("p2")}']");
            await Expect(p2).ToBeVisibleAsync();
            var heights = await Page.EvaluateAsync<double[]>("() => [...document.querySelectorAll('[data-paragraph-id], [data-item-id], [role=treeitem]')].map((e) => e.getBoundingClientRect().height)");
            Log($"{mode}: {string.Join(", ", heights)}");
        }
    }
}
