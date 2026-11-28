using Microsoft.Playwright;
using Read2Me.E2eTests.Infrastructure;

namespace Read2Me.E2eTests.Tests.Web;

/// <summary>Throwaway proof for #482: the skip link takes the first Tab and moves focus to main in place.</summary>
public class SkipLinkProofTests(E2eAppFixture app, PlaywrightFixture pw) : E2eTestBase(app, pw)
{
    [Fact]
    public async Task The_first_Tab_shows_the_skip_link_and_Enter_focuses_main_without_navigating()
    {
        await App.SeedMisSplitParagraphProjectAsync("skip-link", "Skip Link", "A. Author");
        await GotoAppAsync("projects/skip-link/book");
        var skip = Page.Locator(".r2m-skip-link");
        await Expect(skip).ToBeAttachedAsync();
        await Expect(Page.GetByRole(AriaRole.Group, new() { Name = "Reader" })).ToBeVisibleAsync();

        // The first link in the accessibility tree.
        var firstLink = Page.GetByRole(AriaRole.Link).First;
        await Expect(firstLink).ToHaveTextAsync("Skip to main content");

        await Page.Keyboard.PressAsync("Tab");
        await Expect(skip).ToBeFocusedAsync();
        await Expect(skip).ToBeVisibleAsync();
        var box = await skip.BoundingBoxAsync();
        Assert.NotNull(box);
        Assert.True(box!.Width > 20 && box.Height > 10, $"skip link box {box.Width}x{box.Height}");

        var before = Page.Url;
        await Page.Keyboard.PressAsync("Enter");
        Assert.Equal("MAIN", await Page.EvaluateAsync<string>("() => document.activeElement?.tagName"));
        Assert.Equal(before, await Page.EvaluateAsync<string>("() => location.href"));

        await Page.Keyboard.PressAsync("Tab");
        Assert.True(await Page.EvaluateAsync<bool>(
            "() => document.activeElement !== document.querySelector('main') && !!document.activeElement?.closest('main')"));
        // Back off it, the link is hidden again.
        await Expect(skip).ToHaveCSSAsync("position", "absolute");
    }
}
