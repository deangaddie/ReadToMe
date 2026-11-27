using Microsoft.Playwright;
using Read2Me.E2eTests.Infrastructure;

namespace Read2Me.E2eTests.Tests.Web;

/// <summary>Throwaway proof for #480: the breadcrumb trail is a list of links, and the current crumb never truncates.</summary>
public class BreadcrumbsProofTests(E2eAppFixture app, PlaywrightFixture pw) : E2eTestBase(app, pw)
{
    private static readonly string Browser = Environment.GetEnvironmentVariable("R2M_E2E_BROWSER") ?? "chromium";
    private const string Title = "The Life and Strange Surprizing Adventures of Robinson Crusoe, of York, Mariner";

    [Fact]
    public async Task Trail_is_a_list_of_links_and_the_current_crumb_is_never_clipped()
    {
        await App.SeedProjectAsync("crusoe", Title, "Daniel Defoe");
        // The shell's narrowest width that still shows the trail (it hides under 900 px).
        await Page.SetViewportSizeAsync(900, 700);

        // Overview: the long title is the current page.
        await GotoAppAsync("projects/crusoe");
        var nav = Page.GetByRole(AriaRole.Navigation, new() { Name = "Breadcrumbs" });
        await Expect(nav.GetByRole(AriaRole.Link, new() { Name = Title })).ToBeVisibleAsync();
        var snapshot = await nav.AriaSnapshotAsync();
        Console.WriteLine($"[{Browser}] overview:\n{snapshot}");
        await Expect(nav).ToMatchAriaSnapshotAsync($"""
            - navigation "Breadcrumbs":
              - list:
                - listitem:
                  - link "Projects"
                - listitem:
                  - link "{Title}"
            """);
        Assert.DoesNotContain("›", snapshot);
        var current = nav.Locator("a[aria-current='page']");
        var (scroll, client) = await WidthsAsync(current);
        Console.WriteLine($"[{Browser}] overview current: scrollWidth={scroll} clientWidth={client}");
        Assert.True(scroll <= client, $"current crumb clipped: {scroll} > {client}");
        await AssertNoOverlapAsync(nav);
        await Page.Locator(".shell__appbar").ScreenshotAsync(new() { Path = Path.Combine(Path.GetTempPath(), $"crumbs-overview-{Browser}.png") });

        // Book: the long title is an ancestor; it truncates and focus shows its full text.
        await GotoAppAsync("projects/crusoe/book");
        await Expect(nav.Locator("a[aria-current='page']")).ToHaveTextAsync("Book");
        await Expect(nav).ToMatchAriaSnapshotAsync($"""
            - navigation "Breadcrumbs":
              - list:
                - listitem:
                  - link "Projects"
                - listitem:
                  - link "{Title}"
                - listitem:
                  - link "Book"
            """);
        var (bookScroll, bookClient) = await WidthsAsync(nav.Locator("a[aria-current='page']"));
        Assert.True(bookScroll <= bookClient, $"current crumb clipped: {bookScroll} > {bookClient}");

        await AssertNoOverlapAsync(nav);

        // Make room tight enough that the ancestor truncates, then focus it.
        var ancestor = nav.GetByRole(AriaRole.Link, new() { Name = Title });
        var (aScroll, aClient) = await WidthsAsync(ancestor);
        Console.WriteLine($"[{Browser}] book ancestor: scrollWidth={aScroll} clientWidth={aClient}");
        Assert.True(aScroll > aClient, "the ancestor crumb should be truncated at this width");
        await ancestor.FocusAsync();
        var tip = Page.Locator(".r2m-tooltip");
        await Expect(tip).ToBeVisibleAsync();
        await Expect(tip).ToHaveTextAsync(Title);
        var underline = await ancestor.EvaluateAsync<string>("e => getComputedStyle(e).textDecorationLine");
        Console.WriteLine($"[{Browser}] focused ancestor text-decoration-line={underline}");
        await Page.Keyboard.PressAsync("Escape");

        await ancestor.HoverAsync();
        await Expect(tip).ToBeVisibleAsync();
        await Expect(tip).ToHaveTextAsync(Title);
        Assert.Equal("underline", await ancestor.EvaluateAsync<string>("e => getComputedStyle(e).textDecorationLine"));
        await Page.Locator(".shell__appbar").ScreenshotAsync(new() { Path = Path.Combine(Path.GetTempPath(), $"crumbs-{Browser}.png") });
    }

    // The trail ends before the bar's spacer, so a long current title never runs under the controls.
    private async Task AssertNoOverlapAsync(ILocator nav)
    {
        var navBox = (await nav.BoundingBoxAsync())!;
        var spacer = (await Page.Locator(".shell__spacer").BoundingBoxAsync())!;
        var current = (await nav.Locator("a[aria-current='page']").BoundingBoxAsync())!;
        Assert.True(current.X + current.Width <= spacer.X + 0.5, $"current crumb (right={current.X + current.Width}) runs past the spacer (x={spacer.X})");
        Assert.True(navBox.Y >= 0 && current.Height <= 48, $"current crumb height {current.Height} overflows the bar");
    }

    private static async Task<(int Scroll, int Client)> WidthsAsync(ILocator el)
    {
        var w = await el.EvaluateAsync<int[]>("e => [e.scrollWidth, e.clientWidth]");
        return (w[0], w[1]);
    }
}
