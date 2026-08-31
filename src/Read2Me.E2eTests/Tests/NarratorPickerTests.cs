using Microsoft.Playwright;
using Read2Me.E2eTests.Infrastructure;

namespace Read2Me.E2eTests.Tests;

/// <summary>
/// The whole feature through the UI: the narrator is one pinned entry in the character picker, and
/// assigning an item to it or away from it flips what the row shows and what the item counts as
/// (ADR-0006). Three components have to agree — the picker, the row's speaker-derived presentation,
/// and the audio-mode checkbox that only a Generatable item may tick — which no unit test covers
/// together.
/// </summary>
[Collection(E2eCollection.Name)]
public class NarratorPickerTests(E2eAppFixture app, PlaywrightFixture pw) : E2eTestBase(app, pw)
{
    [Fact]
    public async Task Narration_item_flips_to_a_character_and_back_through_the_picker()
    {
        await App.SeedThreeDialogParagraphProjectAsync(
            "narrator-picker", "Narrator Picker Book", "A. Author", characterName: "Alice");

        await GotoAsync("/project/narrator-picker");
        await OpenAttributionTreeAsync();

        var narrationChips = Page.Locator(".paragraph-item-hover-row .mud-chip", new() { HasText = "Narration" });
        await Expect(narrationChips).ToHaveCountAsync(1);

        // Flip the narration item to Alice.
        await narrationChips.First.ClickAsync();
        await Page.Locator(".mud-menu-item", new() { HasText = "Alice" }).First.ClickAsync();

        var aliceChips = Page.Locator(".paragraph-item-hover-row .mud-chip", new() { HasText = "Alice" });
        await Expect(aliceChips).ToHaveCountAsync(1);
        await Expect(narrationChips).ToHaveCountAsync(0);

        // It reads in Alice's voice now, so it is an item audio generation may be asked for.
        await Page.GetByText("Split: Audio").ClickAsync();
        await Expect(Page.Locator(".paragraph-item-hover-row .mud-checkbox input[type=checkbox]:not([disabled])"))
            .ToHaveCountAsync(1);

        // Flip it back through the pinned narrator entry: the narration presentation returns.
        await Page.GetByText("Split: Attribution").ClickAsync();
        await aliceChips.First.ClickAsync();
        await Page.Locator("[data-testid='pick-narrator']").ClickAsync();
        await Expect(narrationChips).ToHaveCountAsync(1);

        // Only a fresh read proves the flips reached the database rather than the in-memory stamp.
        await GotoAsync("/project/narrator-picker");
        await OpenAttributionTreeAsync();
        await Expect(narrationChips).ToHaveCountAsync(1);
        await Expect(aliceChips).ToHaveCountAsync(0);
    }

    /// <summary>Switches to attribution mode and expands ch1, animation settled.</summary>
    private async Task OpenAttributionTreeAsync()
    {
        await Page.GetByText("Split: Attribution").ClickAsync();
        await Page.GetByText("ch1").ClickAsync();
        await Expect(Page.Locator(".mud-collapse-entering")).ToHaveCountAsync(0);
    }
}
