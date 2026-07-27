using Microsoft.Playwright;
using Read2Me.E2eTests.Infrastructure;

namespace Read2Me.E2eTests.Tests;

/// <summary>
/// Bulk assign spans two components no unit test covers together: the switch that arms it lives in
/// StatusDock, the chip click that fires it lives in ParagraphRow, and a confirm dialog sits between
/// them. One happy path — the off-selection rule, the clear case and the short-circuit are unit-tested.
/// </summary>
[Collection(E2eCollection.Name)]
public class BulkCharacterAssignTests(E2eAppFixture app, PlaywrightFixture pw) : E2eTestBase(app, pw)
{
    [Fact]
    public async Task Bulk_assign_applies_one_chip_pick_across_the_whole_selection()
    {
        await App.SeedThreeDialogParagraphProjectAsync(
            "bulk-book", "Bulk Book", "A. Author", characterName: "Alice");

        await GotoAsync("/project/bulk-book");
        await OpenAttributionTreeAsync();

        var unknownChips = Page.Locator(".mud-chip", new() { HasText = "Unknown" });
        await Expect(unknownChips).ToHaveCountAsync(3);

        // Only dialog paragraphs carry a row checkbox in attribution mode, so these are the three
        // (scoped to paragraph blocks — the tree's own node roll-up checkbox is not one of them).
        var rowChecks = Page.Locator(".paragraph-hover-block .mud-checkbox input[type=checkbox]");
        await Expect(rowChecks).ToHaveCountAsync(3);
        for (var i = 0; i < 3; i++)
        {
            var expected = Page.GetByText($"{i + 1} paragraph{(i == 0 ? "" : "s")} selected");
            await ClickUntilAsync(rowChecks.Nth(i), () => Expect(expected).ToBeVisibleAsync());
        }

        // Arm bulk mode from the dock bar.
        var bulkSwitch = Page.GetByRole(AriaRole.Switch, new() { Name = "Bulk assign" });
        await ClickUntilAsync(bulkSwitch, () => Expect(bulkSwitch).ToBeCheckedAsync());

        // One chip pick, on one of the three selected rows.
        await unknownChips.First.ClickAsync();
        await Page.Locator(".mud-menu-item", new() { HasText = "Alice" }).ClickAsync();

        // The whole selection, not the picked row, and an assign rather than a clear.
        await Expect(Page.Locator("[data-testid='confirm-message']"))
            .ToContainTextAsync("Alice becomes the speaker for 3 dialog lines in 3 paragraphs");
        await Page.Locator("[data-testid='confirm-ok']").ClickAsync();

        // All three rows follow the single pick.
        var rowChips = Page.Locator(".paragraph-hover-block .mud-chip");
        await Expect(rowChips.Filter(new() { HasText = "Alice" })).ToHaveCountAsync(3);
        await Expect(unknownChips).ToHaveCountAsync(0);

        // Selection and arming survive the apply.
        await Expect(Page.GetByText("3 paragraphs selected")).ToBeVisibleAsync();
        await Expect(bulkSwitch).ToBeCheckedAsync();

        // Reload: the chips above are the in-memory stamp, so only a fresh read proves the bulk
        // write reached the database.
        await GotoAsync("/project/bulk-book");
        await OpenAttributionTreeAsync();
        await Expect(rowChips.Filter(new() { HasText = "Alice" })).ToHaveCountAsync(3);
        await Expect(unknownChips).ToHaveCountAsync(0);
    }

    /// <summary>
    /// Clicks until the app agrees it happened. Clicks on MudBlazor's boolean inputs must be forced
    /// (the real input sits under a styled span), which skips Playwright's stability wait, and a
    /// forced click on a row the circuit is still re-rendering is simply dropped — so a lost click
    /// is retried rather than failing the test. Re-clicking a click that merely landed late toggles
    /// it off; the next attempt puts it back, so the loop converges either way.
    /// </summary>
    private static async Task ClickUntilAsync(ILocator target, Func<Task> settled)
    {
        for (var attempt = 1; ; attempt++)
        {
            await target.ClickAsync(new() { Force = true });
            try
            {
                await settled();
                return;
            }
            catch (PlaywrightException) when (attempt < 3)
            {
                // Dropped click — go round again.
            }
        }
    }

    /// <summary>Switches to attribution mode and expands ch1, animation settled.</summary>
    private async Task OpenAttributionTreeAsync()
    {
        // Combined is the default view; bulk assign is an attribution-mode feature.
        await Page.GetByText("Split: Attribution").ClickAsync();

        // Chapters are collapsed by default; expand ch1 to lazy-load its paragraphs.
        await Page.GetByText("ch1").ClickAsync();

        // The expansion panel animates open. Clicks below are forced (MudBlazor's real input sits
        // under a styled span), which skips Playwright's stability wait — so wait the animation out
        // here, or a click lands on a row that is still moving and is lost.
        await Expect(Page.Locator(".mud-collapse-entering")).ToHaveCountAsync(0);
    }
}
