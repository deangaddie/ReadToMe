using System.Text.Json;
using Microsoft.Playwright;
using Read2Me.E2eTests.Infrastructure;

namespace Read2Me.E2eTests.Tests.Web;

/// <summary>Throwaway proof for #492: the app chrome grows with the user's default font size.</summary>
public class TextScaleProofTests(E2eAppFixture app, PlaywrightFixture pw) : E2eTestBase(app, pw)
{
    private readonly E2eAppFixture _app = app;
    private readonly PlaywrightFixture _pw = pw;

    /// <summary>A browser whose default font size is 32 px, set the way a user sets it.</summary>
    private async Task<(IBrowserContext Context, IPage Page, Func<Task> Close)> OpenAt32pxAsync()
    {
        var playwright = await Playwright.CreateAsync();
        var viewport = new ViewportSize { Width = 2400, Height = 1400 }; // over 56.25em at 32 px: the wide shell
        if (_pw.IsFirefox)
        {
            var browser = await playwright.Firefox.LaunchAsync(new()
            {
                FirefoxUserPrefs = new Dictionary<string, object>
                {
                    ["font.size.variable.x-western"] = 32,
                    ["ui.primaryPointerCapabilities"] = 6,
                    ["ui.allPointerCapabilities"] = 6,
                },
            });
            var context = await browser.NewContextAsync(new() { ViewportSize = viewport, BaseURL = _app.BaseUrl });
            var page = await context.NewPageAsync();
            return (context, page, async () => { await browser.DisposeAsync(); playwright.Dispose(); });
        }
        else
        {
            // The launch preference (a profile Preferences file with webkit.webprefs.default_font_size) does not
            // survive Playwright's launch, so set the same WebPreferences value over CDP: Page.setFontSizes.
            var browser = await playwright.Chromium.LaunchAsync();
            var context = await browser.NewContextAsync(new() { ViewportSize = viewport, BaseURL = _app.BaseUrl });
            var page = await context.NewPageAsync();
            var cdp = await context.NewCDPSessionAsync(page);
            await cdp.SendAsync("Page.setFontSizes", new Dictionary<string, object> { ["fontSizes"] = new Dictionary<string, object> { ["standard"] = 32 } });
            return (context, page, async () => { await browser.DisposeAsync(); playwright.Dispose(); });
        }
    }

    private static async Task GotoAsync(IPage page, string path)
    {
        await page.GotoAsync(AppPath(path));
        await Expect(page.Locator("app-root[data-live-state='connected']")).ToBeAttachedAsync(new() { Timeout = 15_000 });
    }

    private const string Clipped = """
        sel => [...document.querySelectorAll(sel)]
          .filter(el => el.getClientRects().length > 0)
          .map(el => ({ el: (el.className || el.tagName) + ' ' + (el.textContent || '').trim().slice(0, 30),
                        sh: el.scrollHeight, ch: el.clientHeight }))
        """;

    [Fact]
    public async Task At_a_32px_default_font_size_the_bars_hold_every_control_and_nothing_clips()
    {
        await App.SeedThreeDialogParagraphProjectAsync("text-scale", "Text Scale A Rather Long Book Title", "A. Author");
        var (_, page, close) = await OpenAt32pxAsync();
        try
        {
            await GotoAsync(page, "projects/text-scale/book");
            await Expect(page.Locator(".shell__crumbs")).ToBeVisibleAsync();

            var rootSize = await page.EvaluateAsync<string>("() => getComputedStyle(document.documentElement).fontSize");
            Console.WriteLine($"{_pw.BrowserName}: root font-size {rootSize}");
            Assert.Equal("32px", rootSize);

            var iconSize = await page.Locator(".shell__appbar .material-symbols-rounded").First
                .EvaluateAsync<string>("el => getComputedStyle(el).fontSize");
            Console.WriteLine($"app-bar icon font-size {iconSize}");
            Assert.Equal("40px", iconSize);

            foreach (var bar in new[] { ".shell__appbar", ".shell__activity" })
            {
                var h = await page.Locator(bar).EvaluateAsync<double>("el => el.getBoundingClientRect().height");
                Console.WriteLine($"{bar} height {h}");
            }
            // Every control sits inside its bar: the bar is as tall as its tallest control, never cut by a fixed height.
            var outside = await page.EvaluateAsync<string[]>("""
                () => [".shell__appbar", ".shell__activity"].flatMap(sel => {
                  const bar = document.querySelector(sel).getBoundingClientRect();
                  return [...document.querySelectorAll(`${sel} *`)].filter(el => el.getClientRects().length > 0 && !el.closest(".visually-hidden"))
                    .filter(el => { const r = el.getBoundingClientRect(); return r.top < bar.top - 0.5 || r.bottom > bar.bottom + 0.5; })
                    .map(el => `${sel}: ${el.className || el.tagName}`);
                })
                """);
            Assert.Empty(outside);

            const string controls =
                ".shell__appbar button, .shell__appbar a, .shell__crumbs, .shell__crumbs li, .shell__crumb, .shell__conn, .shell__activity > *, .shell__activity button";
            var all = await page.EvaluateAsync<JsonElement>(Clipped, controls);
            Console.WriteLine($"checked {all.GetArrayLength()} app-bar and activity-bar controls");
            var bad = all.EnumerateArray().Where(c => c.GetProperty("sh").GetInt32() > c.GetProperty("ch").GetInt32()).ToList();
            foreach (var c in all.EnumerateArray())
                Console.WriteLine($"  {c.GetProperty("el").GetString()}: scrollHeight {c.GetProperty("sh")} clientHeight {c.GetProperty("ch")}");
            Assert.Empty(bad);

            // A button is at least as tall as its content: its padding box holds the box of every child,
            // the label text included (an icon's own glyph area, wider than its 1em line, is not the button's).
            var buttons = await page.EvaluateAsync<JsonElement>("""
                () => [...document.querySelectorAll('.r2m-button')].filter(b => b.getClientRects().length > 0).map(b => {
                  const boxes = [...b.childNodes].flatMap(n => {
                    if (n.nodeType === Node.ELEMENT_NODE) return [n.getBoundingClientRect()];
                    if (n.nodeType !== Node.TEXT_NODE || !n.textContent.trim()) return [];
                    const range = document.createRange(); range.selectNodeContents(n); return [range.getBoundingClientRect()];
                  }).filter(x => x.height > 0);
                  const top = Math.min(...boxes.map(x => x.top)), bottom = Math.max(...boxes.map(x => x.bottom));
                  const r = b.getBoundingClientRect();
                  return { el: b.className + ' ' + b.textContent.trim().slice(0, 30), h: b.clientHeight, content: bottom - top,
                           inside: top >= r.top + b.clientTop - 0.5 && bottom <= r.bottom - b.clientTop + 0.5 };
                })
                """);
            Console.WriteLine($"checked {buttons.GetArrayLength()} .r2m-button on the reader");
            foreach (var b in buttons.EnumerateArray())
                Console.WriteLine($"  {b.GetProperty("el").GetString()}: clientHeight {b.GetProperty("h")} content {b.GetProperty("content")}");
            Assert.True(buttons.GetArrayLength() > 0);
            Assert.All(buttons.EnumerateArray(), b => Assert.True(b.GetProperty("inside").GetBoolean(),
                $"{b.GetProperty("el").GetString()} is shorter than its content"));
        }
        finally { await close(); }
    }

    [Fact]
    public async Task A_long_button_label_that_wraps_stays_inside_its_button()
    {
        await GotoAppAsync("settings/llm");
        var box = await Page.EvaluateAsync<JsonElement>("""
            () => {
              const b = document.createElement('button');
              b.className = 'r2m-button r2m-button--stroked';
              b.style.width = '140px';
              b.textContent = 'A very long button label that has to wrap onto several lines';
              document.querySelector('.shell__main').prepend(b);
              const r = b.getBoundingClientRect();
              const range = document.createRange(); range.selectNodeContents(b);
              const t = range.getBoundingClientRect();
              return { top: r.top, bottom: r.bottom, h: r.height, tTop: t.top, tBottom: t.bottom, sh: b.scrollHeight, ch: b.clientHeight };
            }
            """);
        Console.WriteLine($"{_pw.BrowserName}: wrapped button {box}");
        Assert.True(box.GetProperty("h").GetDouble() > 36, "the button should grow past 36 px");
        Assert.True(box.GetProperty("tTop").GetDouble() >= box.GetProperty("top").GetDouble());
        Assert.True(box.GetProperty("tBottom").GetDouble() <= box.GetProperty("bottom").GetDouble());
        Assert.True(box.GetProperty("sh").GetInt32() <= box.GetProperty("ch").GetInt32());
    }

    /// <summary>At the default size: screenshots for the before/after comparison, into R2M_PROOF_SHOTS.</summary>
    [Fact]
    public async Task Default_size_screenshots()
    {
        var dir = Environment.GetEnvironmentVariable("R2M_PROOF_SHOTS");
        if (string.IsNullOrEmpty(dir)) return;
        Directory.CreateDirectory(dir);
        await App.SeedThreeDialogParagraphProjectAsync("text-shots", "Text Shots", "A. Author");
        var still = "*, *::before, *::after { transition: none !important; animation: none !important; caret-color: transparent !important; }";

        await GotoAppAsync("projects/text-shots/book");
        await Page.AddStyleTagAsync(new() { Content = still });
        await Page.WaitForTimeoutAsync(1000);
        await Page.Locator(".shell__appbar").ScreenshotAsync(new() { Path = Path.Combine(dir, $"{_pw.BrowserName}-appbar.png") });
        await Page.ScreenshotAsync(new() { Path = Path.Combine(dir, $"{_pw.BrowserName}-reader.png") });

        await GotoAppAsync("settings/llm");
        await Page.AddStyleTagAsync(new() { Content = still });
        await Page.WaitForTimeoutAsync(1000);
        await Page.ScreenshotAsync(new() { Path = Path.Combine(dir, $"{_pw.BrowserName}-settings.png") });
    }
}
