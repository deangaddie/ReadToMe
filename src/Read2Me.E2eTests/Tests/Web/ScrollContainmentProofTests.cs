using System.Net;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Playwright;
using Read2Me.E2eTests.Infrastructure;

namespace Read2Me.E2eTests.Tests.Web;

/// <summary>
/// Throwaway proof for #501: the page scroller keeps a stable scrollbar gutter, a config list
/// wheeled past its end doesn't scroll the page, and a region with nothing to scroll doesn't hold
/// the wheel back from the page.
/// <para>
/// Playwright's Firefox synthesises the wheel on a path that ignores <c>overscroll-behavior</c>,
/// so in Firefox the wheel is a native <c>WM_MOUSEWHEEL</c> posted to the headed window (Windows
/// only, <c>E2E_HEADED=1</c>; the user's pointer doesn't move).
/// </para>
/// </summary>
public class ScrollContainmentProofTests(E2eAppFixture app, PlaywrightFixture pw, ITestOutputHelper output)
    : E2eTestBase(app, pw)
{
    private static readonly HttpClient Http = new();
    private readonly bool _firefox = pw.IsFirefox;
    private readonly string _browser = $"{pw.BrowserName} {pw.Browser.Version}";

    // Needs a real browser: scrollbar space is real layout.
    [Fact]
    public async Task Main_keeps_its_width_when_the_page_starts_to_overflow()
    {
        await App.SeedProjectAsync("scroll-gutter", "Scroll Gutter", "A. Author");
        await Page.SetViewportSizeAsync(1280, 800);
        await GotoAppAsync("projects");
        await Expect(Page.GetByText("Scroll Gutter").First).ToBeVisibleAsync();
        var main = Page.Locator(".shell__main");

        Assert.False(await Overflows(main), "the page should start short");
        var before = await main.EvaluateAsync<int>("el => el.clientWidth");
        await main.EvaluateAsync(
            "el => { const tall = document.createElement('div'); tall.style.height = '3000px'; el.append(tall); }");
        Assert.True(await Overflows(main), "the page should now overflow");
        var after = await main.EvaluateAsync<int>("el => el.clientWidth");
        var gutter = await Computed(main, "scrollbar-gutter");

        output.WriteLine($"{_browser}: clientWidth {before} -> {after}, scrollbar-gutter {gutter}");
        Assert.Equal(before, after);
        Assert.Equal("stable", gutter);

        await GotoAppAsync("projects/scroll-gutter");
        await Page.GetByRole(AriaRole.Button, new() { Name = "History", Exact = true }).ClickAsync();
        var sheet = Page.Locator(".shell__history");
        await Expect(sheet).ToBeVisibleAsync();
        var behaviour = await Computed(sheet, "overscroll-behavior-y");
        output.WriteLine($"{_browser}: History sheet overscroll-behavior-y {behaviour}");
        Assert.Equal("contain", behaviour);
    }

    // Needs a real browser: scroll chaining is the engine's own wheel handling.
    [Fact]
    public async Task Wheeling_past_the_end_of_the_LLM_list_leaves_the_page_where_it_is()
    {
        const string list = ".llm__list";
        for (var i = 0; i < 30; i++)
        {
            var created = await Http.PostAsJsonAsync(
                $"{App.BaseUrl}/api/settings/llm", new { name = $"scroll {i:00}", baseUrl = "http://fake-llm" });
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        }

        await Page.SetViewportSizeAsync(1280, 800);
        await GotoAppAsync("settings/llm");
        var main = Page.Locator(".shell__main");
        var rows = Page.Locator($"{list} .r2m-config-list__rows");
        await Expect(rows.Locator(".r2m-config-list__row").Nth(30)).ToBeAttachedAsync();
        Assert.True(await Overflows(rows), "the list should scroll");
        Assert.True(await Overflows(main), "the page should scroll too");
        await Expect(rows).ToHaveAttributeAsync("data-overflows", "");

        await rows.EvaluateAsync("el => { el.scrollTop = el.scrollHeight; }");
        var contained = await WheelOver(rows, main);
        output.WriteLine($"{_browser}: page scrollTop after wheeling past the end of {list}: {contained}");
        Assert.Equal(0, contained);

        // The control: the same wheel without the rule does chain into the page. A fresh load, since
        // Chromium keeps a wheel latched to the scroller the last one went to.
        await GotoAppAsync("settings/llm");
        await Expect(rows).ToHaveAttributeAsync("data-overflows", "");
        // Two frames, so the compositor has the new style before the wheel arrives.
        await rows.EvaluateAsync(
            "el => { el.removeAttribute('data-overflows'); el.scrollTop = el.scrollHeight; return new Promise(done => requestAnimationFrame(() => requestAnimationFrame(done))); }");
        var chained = await WheelOver(rows, main);
        output.WriteLine($"{_browser}: control, the same wheel without containment: {chained}");
        Assert.True(chained > 0, "without containment the wheel should reach the page");
    }

    // Needs a real browser: engines contain a scroll container even with nothing to scroll.
    [Theory]
    [InlineData(".llm__list .r2m-config-list__rows")]
    [InlineData(".r2m-config-editor-frame__body")]
    public async Task Wheeling_over_a_region_with_nothing_to_scroll_still_scrolls_the_page(string region)
    {
        await Page.SetViewportSizeAsync(1280, 500);
        await GotoAppAsync("settings/llm");
        var main = Page.Locator(".shell__main");
        await Page.Locator(".llm__list .r2m-config-list__main").First.ClickAsync();
        var target = Page.Locator(region);
        await Expect(target).ToBeVisibleAsync();
        // Earlier tests in the class may have filled the list; filter it down to one row.
        await Page.GetByLabel("Search configurations").FillAsync("fake");
        await Expect(Page.Locator(".llm__list .r2m-config-list__row")).ToHaveCountAsync(1);
        Assert.False(await Overflows(target), $"{region} should have nothing to scroll");
        Assert.True(await Overflows(main), "the page should scroll");

        var scrolled = await WheelOver(target, main);

        output.WriteLine($"{_browser}: page scrollTop after wheeling over {region}: {scrolled}");
        Assert.True(scrolled > 0, $"wheeling over {region} should scroll the page");
    }

    private static Task<bool> Overflows(ILocator locator) =>
        locator.EvaluateAsync<bool>("el => el.scrollHeight > el.clientHeight");

    private static Task<string> Computed(ILocator locator, string property) =>
        locator.EvaluateAsync<string>("(el, name) => getComputedStyle(el).getPropertyValue(name)", property);

    private async Task<int> WheelOver(ILocator over, ILocator main)
    {
        await main.EvaluateAsync("el => { el.scrollTop = 0; }");
        var box = (await over.BoundingBoxAsync())!;
        var x = box.X + box.Width / 2;
        var y = Math.Min(box.Y + box.Height / 2, Page.ViewportSize!.Height - 80);
        if (_firefox) await NativeWheelDown(x, y);
        else
        {
            await Page.Mouse.MoveAsync(x, y);
            await Page.Mouse.WheelAsync(0, 300);
        }
        // Nothing signals "the wheel did not scroll", so give the engine time to have done it.
        await Page.WaitForTimeoutAsync(1500);
        return await main.EvaluateAsync<int>("el => Math.round(el.scrollTop)");
    }

    /// <summary>Three wheel notches down at a viewport point, as Windows would deliver them.</summary>
    private async Task NativeWheelDown(float x, float y)
    {
        Assert.SkipUnless(
            OperatingSystem.IsWindows() && Environment.GetEnvironmentVariable("E2E_HEADED") == "1",
            "the Firefox wheel proof needs a headed browser on Windows (E2E_HEADED=1)");
        SetProcessDPIAware();
        var marker = $"r2m-proof-{Guid.NewGuid():N}";
        await Page.EvaluateAsync("title => { document.title = title; }", marker);
        await Page.BringToFrontAsync();
        await Page.WaitForTimeoutAsync(1000);
        var window = IntPtr.Zero;
        EnumWindows((handle, _) =>
        {
            var title = new StringBuilder(256);
            GetWindowText(handle, title, title.Capacity);
            if (title.ToString().StartsWith(marker, StringComparison.Ordinal)) window = handle;
            return true;
        }, IntPtr.Zero);
        Assert.NotEqual(IntPtr.Zero, window);
        // Topmost (NOSIZE | NOMOVE | NOACTIVATE), so the point belongs to this window.
        SetWindowPos(window, new IntPtr(-1), 0, 0, 0, 0, 0x0013);

        var at = await Page.EvaluateAsync<int[]>(
            "([x, y]) => [Math.round((mozInnerScreenX + x) * devicePixelRatio), Math.round((mozInnerScreenY + y) * devicePixelRatio)]",
            new double[] { x, y });
        const uint mouseWheel = 0x020A;
        var notchDown = new IntPtr(unchecked((int)0xFF880000));
        var point = new IntPtr((at[1] << 16) | (at[0] & 0xFFFF));
        for (var notch = 0; notch < 3; notch++)
        {
            PostMessage(window, mouseWheel, notchDown, point);
            await Page.WaitForTimeoutAsync(200);
        }
    }

    private delegate bool WindowProc(IntPtr window, IntPtr parameter);

    [DllImport("user32.dll")] private static extern bool EnumWindows(WindowProc callback, IntPtr parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, StringBuilder text, int count);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
}
