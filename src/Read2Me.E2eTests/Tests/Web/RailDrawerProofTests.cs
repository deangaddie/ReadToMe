using Read2Me.E2eTests.Infrastructure;

namespace Read2Me.E2eTests.Tests.Web;

/// <summary>Throwaway proof for #469: the narrow-screen rail is a modal drawer.</summary>
public class RailDrawerProofTests(E2eAppFixture app, PlaywrightFixture pw) : E2eTestBase(app, pw)
{
    // Needs a real browser: real focus movement and Tab order through inert.
    [Fact]
    public async Task The_narrow_rail_traps_focus_and_closes_on_Escape()
    {
        await Page.SetViewportSizeAsync(600, 800);
        await GotoAppAsync("projects");
        var toggle = Page.Locator(".shell__menu");
        await Expect(toggle).ToHaveAttributeAsync("aria-expanded", "false");

        await toggle.ClickAsync();
        await Expect(Page.Locator(".shell__rail--open")).ToBeVisibleAsync();
        await Expect(toggle).ToHaveAttributeAsync("aria-expanded", "true");
        var links = Page.Locator(".shell__nav-item");
        await Expect(links.First).ToBeFocusedAsync();

        await links.Last.FocusAsync();
        await Page.Keyboard.PressAsync("Tab");
        var inMain = await Page.EvaluateAsync<bool>("() => !!document.activeElement?.closest('.shell__main')");
        var where = await Page.EvaluateAsync<string>("() => document.activeElement?.outerHTML.slice(0, 80) ?? 'none'");
        Assert.False(inMain, $"focus reached .shell__main: {where}");

        await Page.Keyboard.PressAsync("Escape");
        await Expect(Page.Locator(".shell__rail--closed")).ToHaveCountAsync(1);
        await Expect(toggle).ToBeFocusedAsync();
        await Expect(toggle).ToHaveAttributeAsync("aria-expanded", "false");
    }
}
