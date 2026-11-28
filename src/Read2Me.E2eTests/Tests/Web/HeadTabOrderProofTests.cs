using Microsoft.Playwright;
using Read2Me.E2eTests.Infrastructure;

namespace Read2Me.E2eTests.Tests.Web;

/// <summary>Throwaway proof for #497: Tab through the Structure step's head on the Overview follows its visual (reading) order, wide and narrow. Deleted in the PR's final commit.</summary>
public class HeadTabOrderProofTests(E2eAppFixture app, PlaywrightFixture pw) : E2eTestBase(app, pw)
{
    private static string Engine => Environment.GetEnvironmentVariable("R2M_E2E_BROWSER") == "firefox" ? "firefox" : "chromium";

    [Theory]
    [InlineData(1280)]
    [InlineData(800)]
    public async Task Tab_through_the_structure_head_follows_reading_order(int width)
    {
        var folder = $"head-tab-{width}-{Guid.NewGuid():N}";
        await App.SeedProjectAsync(folder, "Head Tab", "A. Author");
        await Page.SetViewportSizeAsync(width, 900);
        await GotoAppAsync($"projects/{folder}");

        var head = Page.Locator(".r2m-pipeline__step[data-step='Structure'] .r2m-pipeline__head");
        await Expect(head.GetByRole(AriaRole.Button, new() { Name = "Confirm" })).ToBeVisibleAsync();
        var mainWidth = await Page.Locator(".shell__main").EvaluateAsync<double>("e => { const s = getComputedStyle(e); return e.clientWidth - parseFloat(s.paddingLeft) - parseFloat(s.paddingRight); }");
        var basis = await head.Locator(".r2m-pipeline__checkpoint").EvaluateAsync<string>("e => getComputedStyle(e).flexBasis");

        // Focus the head's first tabbable control, then Tab until focus leaves the head.
        var first = await head.EvaluateAsync<bool>("""
            h => { const c = h.querySelector('button, a[href], [tabindex]:not([tabindex="-1"])'); c?.focus(); return c !== null; }
            """);
        Assert.True(first, "the head has no focusable control");
        var stops = new List<(string Name, double X, double Y, double H)>();
        for (var i = 0; i < 20; i++)
        {
            var stop = await Page.EvaluateAsync<string[]?>("""
                () => {
                  const a = document.activeElement;
                  if (!a || !a.closest('.r2m-pipeline__step[data-step="Structure"] .r2m-pipeline__head')) return null;
                  const r = a.getBoundingClientRect();
                  const name = a.getAttribute('aria-label') ?? a.textContent.trim();
                  return [name, String(r.x), String(r.y), String(r.height)];
                }
                """);
            if (stop is null) break;
            stops.Add((stop[0], double.Parse(stop[1]), double.Parse(stop[2]), double.Parse(stop[3])));
            await Page.Keyboard.PressAsync("Tab");
        }

        var log = new List<string> { $"[{Engine} {width}px, page container {mainWidth:0}px, checkpoint flex-basis {basis}] head Tab stops:" };
        log.AddRange(stops.Select(s => $"  {s.Name,-24} x={s.X:0.0} y={s.Y:0.0} h={s.H:0.0}"));
        foreach (var line in log) Console.WriteLine(line);
        File.AppendAllLines(Path.Combine(Path.GetTempPath(), "r2m-497-stops.txt"), log);

        Assert.True(stops.Count >= 2, $"expected the ? and at least one pill, got {stops.Count}");
        Assert.StartsWith("Help for", stops[0].Name);
        for (var i = 1; i < stops.Count; i++)
        {
            var (prev, cur) = (stops[i - 1], stops[i]);
            var sameLine = cur.Y < prev.Y + prev.H && cur.Y + cur.H > prev.Y;
            var lower = cur.Y >= prev.Y + prev.H - 1;
            Assert.True(sameLine ? cur.X > prev.X : lower,
                $"{cur.Name} (x={cur.X}, y={cur.Y}) comes before {prev.Name} (x={prev.X}, y={prev.Y}) in reading order");
        }
    }
}
