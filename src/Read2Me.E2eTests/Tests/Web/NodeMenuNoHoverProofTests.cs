using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using Read2Me.Data;
using Read2Me.E2eTests.Infrastructure;
using Read2Me.TestUtils;

namespace Read2Me.E2eTests.Tests.Web;

/// <summary>
/// Throwaway proof for #494: on a device without hover every Node menu trigger (paragraph, heading,
/// pause, tree) is visible at rest; with a mouse they stay hidden until hover, focus or an open menu.
/// </summary>
public class NodeMenuNoHoverProofTests(E2eAppFixture app, PlaywrightFixture pw) : E2eTestBase(app, pw)
{
    private static readonly string[] Triggers =
    [
        ".r2m-paragraph__menu",
        ".book__heading-menu",
        ".book__pause-menu",
        ".tree__node:not(.tree__node--current) .tree__menu",
    ];

    private async Task<string> SeedAsync(string folder)
    {
        var factory = App.Services.GetRequiredService<IProjectDbContextFactory>();
        var path = Path.Combine(App.WorkspaceDir, folder);
        var builder = new BookHierarchyBuilder(() => factory.CreateAsync(path));
        builder
            .WithProject(title: "The Call of the Wild", author: "Jack London")
            .AddVolume("v1", v =>
            {
                v.AddChapter("Into the Primitive", c => c
                    .AddParagraph("p1", p => p.AddNarration("n1", "Buck did not read the newspapers."))
                    .AddParagraph("pause", p => p.AddPause("pz"))
                    .AddParagraph("p2", p => p.AddNarration("n2", "Buck lived at a big house in the sun-kissed Santa Clara Valley.")));
                v.AddChapter("The Law of Club and Fang", c => c
                    .AddParagraph("p3", p => p.AddNarration("n3", "Buck's first day on the Dyea beach was like a nightmare.")));
            });
        await builder.BuildAsync();
        return $"projects/{folder}/book?mode=speakers";
    }

    [Fact]
    public async Task On_a_device_without_hover_every_trigger_is_visible_at_rest()
    {
        var path = await SeedAsync("no-hover-touch");
        await GotoAppAsync(path); // stages the bundle

        var firefox = Environment.GetEnvironmentVariable(PlaywrightFixture.BrowserVariable) == "firefox";
        using var playwright = await Playwright.CreateAsync();
        // Playwright's Firefox has no isMobile, and its hover media feature follows the pointer
        // prefs, so a touch Firefox is one whose only pointer is coarse with no hover (bit 1).
        await using var browser = firefox
            ? await playwright.Firefox.LaunchAsync(new()
            {
                FirefoxUserPrefs = new Dictionary<string, object> { ["ui.primaryPointerCapabilities"] = 1, ["ui.allPointerCapabilities"] = 1 },
            })
            : await playwright.Chromium.LaunchAsync();
        await using var context = await browser.NewContextAsync(new()
        {
            ViewportSize = new ViewportSize { Width = 1280, Height = 900 },
            HasTouch = true,
            IsMobile = firefox ? null : true,
        });
        var page = await context.NewPageAsync();
        await page.GotoAsync(App.BaseUrl + AppPath(path));
        await Assertions.Expect(page.Locator("app-root[data-live-state='connected']")).ToBeAttachedAsync(new() { Timeout = 15_000 });

        Assert.True(await page.EvaluateAsync<bool>("() => matchMedia('(hover: none)').matches"));
        foreach (var selector in Triggers)
        {
            var trigger = page.Locator(selector).First;
            await Assertions.Expect(trigger).ToBeAttachedAsync();
            await Assertions.Expect(trigger).ToHaveCSSAsync("opacity", "1");
            Console.WriteLine($"touch {selector}: opacity {await trigger.EvaluateAsync<string>("e => getComputedStyle(e).opacity")}");
        }
    }

    [Fact]
    public async Task With_a_mouse_every_trigger_is_hidden_at_rest()
    {
        var path = await SeedAsync("no-hover-desktop");
        await GotoAppAsync(path);
        await Page.Mouse.MoveAsync(0, 0);

        Assert.False(await Page.EvaluateAsync<bool>("() => matchMedia('(hover: none)').matches"));
        foreach (var selector in Triggers)
        {
            var trigger = Page.Locator(selector).First;
            await Expect(trigger).ToBeAttachedAsync();
            await Expect(trigger).ToHaveCSSAsync("opacity", "0");
            Console.WriteLine($"desktop {selector}: opacity {await trigger.EvaluateAsync<string>("e => getComputedStyle(e).opacity")}");
        }
    }
}
