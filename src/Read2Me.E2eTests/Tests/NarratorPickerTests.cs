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

    /// <summary>
    /// The combined view offers only the paragraph-level picker, and its sweep touches non-narrator
    /// items — so a paragraph assigned wholly to the narrator has nothing left to sweep. Without
    /// the all-narration exception that gesture is a one-way door here, however well the split
    /// views' per-item pickers work.
    /// </summary>
    [Fact]
    public async Task Combined_view_assigns_a_paragraph_to_the_narrator_and_back()
    {
        await App.SeedThreeDialogParagraphProjectAsync(
            "narrator-combined", "Narrator Combined Book", "A. Author", characterName: "Alice");

        await GotoAsync("/project/narrator-combined");
        // Combined is the default view; expand ch1 to lazy-load its paragraphs.
        await Page.GetByText("ch1").ClickAsync();
        await Expect(Page.Locator(".mud-collapse-entering")).ToHaveCountAsync(0);

        var rowChips = Page.Locator(".paragraph-hover-row .mud-chip");
        var unknownChips = rowChips.Filter(new() { HasText = "Unknown" });
        var narrationChips = rowChips.Filter(new() { HasText = "Narration" });
        var aliceChips = rowChips.Filter(new() { HasText = "Alice" });

        // Three dialog paragraphs, plus one that was narration from import.
        await Expect(unknownChips).ToHaveCountAsync(3);
        await Expect(narrationChips).ToHaveCountAsync(1);

        // Dialog paragraph -> narrator.
        await unknownChips.First.ClickAsync();
        await Page.Locator("[data-testid='pick-narrator']").ClickAsync();
        await Expect(narrationChips).ToHaveCountAsync(2);
        await Expect(unknownChips).ToHaveCountAsync(2);

        // ...and back to a character, which is the half that used to be impossible here.
        await narrationChips.First.ClickAsync();
        await Page.Locator(".mud-menu-item", new() { HasText = "Alice" }).First.ClickAsync();
        await Expect(aliceChips).ToHaveCountAsync(1);

        // Only a fresh read proves it reached the database rather than the in-memory stamp.
        await GotoAsync("/project/narrator-combined");
        await Page.GetByText("ch1").ClickAsync();
        await Expect(Page.Locator(".mud-collapse-entering")).ToHaveCountAsync(0);
        await Expect(aliceChips).ToHaveCountAsync(1);
    }

    /// <summary>Switches to attribution mode and expands ch1, animation settled.</summary>
    private async Task OpenAttributionTreeAsync()
    {
        await Page.GetByText("Split: Attribution").ClickAsync();
        await Page.GetByText("ch1").ClickAsync();
        await Expect(Page.Locator(".mud-collapse-entering")).ToHaveCountAsync(0);
    }
}
