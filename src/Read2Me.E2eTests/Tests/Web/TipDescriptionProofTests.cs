using System.Text.Json;
using Microsoft.Playwright;
using Read2Me.E2eTests.Infrastructure;

namespace Read2Me.E2eTests.Tests.Web;

/// <summary>Throwaway proof for #467: a tip that says more than the name is the accessible description.</summary>
public class TipDescriptionProofTests(E2eAppFixture app, PlaywrightFixture pw) : E2eTestBase(app, pw)
{
    // Needs a real browser: the accessibility tree of a running page.
    [Fact]
    public async Task A_status_chip_and_a_count_badge_carry_their_tip_as_a_description()
    {
        const string chipTip = "1 of 1 voice ready for TTS";
        const string badgeTip = "Paragraphs needing attribution";
        await App.SeedThreeDialogParagraphProjectAsync("tip-proof", "Tip Proof", "A. Author");
        await App.SeedNarratorVoiceAsync("tip-proof");

        await GotoAppAsync("projects/tip-proof/cast");
        var chip = Page.Locator($".r2m-status-chip[data-tooltip='{chipTip}']");
        await Expect(chip).ToHaveAccessibleDescriptionAsync(chipTip);
        await AssertHeldByItsTrigger(chip);
        await AssertInChromiumTree(chipTip);

        await GotoAppAsync("projects/tip-proof/book");
        var badge = Page.Locator($".r2m-count-badge[data-tooltip='{badgeTip}']").First;
        await Expect(badge).ToHaveAccessibleDescriptionAsync(badgeTip);
        await AssertHeldByItsTrigger(badge);
        await AssertInChromiumTree(badgeTip);
    }

    /// <summary>The hidden description is positioned against its trigger, so it cannot stretch a scroller.</summary>
    private static async Task AssertHeldByItsTrigger(ILocator trigger) =>
        Assert.True(await trigger.EvaluateAsync<bool>(
            "el => el.querySelector('.visually-hidden').offsetParent === el"));

    /// <summary>
    /// Chromium only: the browser's own accessibility tree (CDP), beside Playwright's computed
    /// description above, which is all Firefox offers a test.
    /// </summary>
    private async Task AssertInChromiumTree(string tip)
    {
        if (Page.Context.Browser!.BrowserType.Name != "chromium")
        {
            TestContext.Current.TestOutputHelper!.WriteLine($"[{Page.Context.Browser.BrowserType.Name}] description: {tip}");
            return;
        }

        var session = await Page.Context.NewCDPSessionAsync(Page);
        var tree = await session.SendAsync("Accessibility.getFullAXTree");
        var described = tree!.Value.GetProperty("nodes").EnumerateArray()
            .Where(n => n.TryGetProperty("description", out var d) && d.GetProperty("value").GetString() == tip)
            .Select(n => n.GetProperty("role").GetProperty("value").GetString())
            .ToArray();
        TestContext.Current.TestOutputHelper!.WriteLine($"[chromium AX tree] description '{tip}' on roles: {string.Join(", ", described)}");
        Assert.NotEmpty(described);
    }
}
