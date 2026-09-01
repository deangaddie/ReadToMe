using Microsoft.Playwright;
using Read2Me.E2eTests.Infrastructure;

namespace Read2Me.E2eTests.Tests;

/// <summary>
/// Insertion is the one gesture that runs the whole chain — item overflow menu, text dialog,
/// <c>InsertParagraphItemCommand</c>, and a full tree reload — so one happy path proves the chain
/// holds together. Before/after, first/last, whitespace refusal and Pause anchors are unit- and
/// component-tested; they are deliberately not repeated here.
/// </summary>
[Collection(E2eCollection.Name)]
public class InsertParagraphItemTests(E2eAppFixture app, PlaywrightFixture pw) : E2eTestBase(app, pw)
{
    [Fact]
    public async Task Insert_item_after_adds_an_unattributed_row_and_leaves_the_anchor_alone()
    {
        // The mis-split fixture is the spec's own scenario: "mixed" holds two speakers because the
        // quote scan merged them. Stamped with a speaker and audio, it is the anchor whose work the
        // insertion must not disturb.
        var book = await App.SeedMisSplitParagraphProjectAsync(
            "insert-book", "Insert Book", "A. Author", characterName: "Alice");
        var anchorId = book.ItemId("mixed");
        await App.SeedItemAudioAsync("insert-book", anchorId, book.CharacterId("Alice"));

        await GotoAsync("/project/insert-book");

        // Audio mode: the only view that shows both the per-item audio checkbox and the play button,
        // so "unattributed" and "the anchor kept its audio" are both readable off the row.
        await Page.GetByText("Split: Audio").ClickAsync();
        await Page.GetByText("ch1").ClickAsync();
        await Expect(Page.Locator(".mud-collapse-entering")).ToHaveCountAsync(0);

        var block = Page.Locator(".paragraph-hover-block")
            .Filter(new() { HasText = "The door swung open." });
        var rows = block.Locator(".paragraph-item-hover-row");
        await Expect(rows).ToHaveCountAsync(3);

        // The node menu is the last icon button on the row (the play button precedes it).
        await rows.Nth(1).Locator("button.mud-icon-button").Last.ClickAsync();
        await Page.Locator(".mud-menu-item", new() { HasText = "Insert Item After" })
            .ClickAsync(new() { Force = true }); // MudBlazor menu items need a forced click.

        const string newText = "“And who might you be?” he answered.";
        var field = Page.Locator(".mud-dialog textarea");
        await Expect(field).ToBeVisibleAsync();
        await field.FillAsync(newText);
        // EditTextDialog binds on change, so blur before the Save button re-evaluates Disabled.
        await field.BlurAsync();
        await Page.GetByRole(AriaRole.Button, new() { Name = "Save" }).ClickAsync();

        // Insertion is structural, so the tree reloads from the database — what is asserted below is
        // already a fresh read, not the in-memory stamp.
        await Expect(rows).ToHaveCountAsync(4);

        // The new row sits immediately after the anchor and carries the typed text.
        await Expect(rows.Nth(2)).ToContainTextAsync(newText);
        await Expect(rows.Nth(3)).ToContainTextAsync("“Only me,” came the reply.");

        // Born unattributed: the warning chip rather than a character, and no audio to select.
        await Expect(rows.Nth(2).Locator(".mud-chip")).ToHaveTextAsync("Unknown");
        await Expect(rows.Nth(2).Locator("input[type=checkbox]")).ToBeDisabledAsync();

        // The anchor is untouched — same text, same speaker, and its audio still plays.
        await Expect(rows.Nth(1)).ToContainTextAsync(
            "“Hello there,” she said. “And who might you be?” he answered.");
        await Expect(rows.Nth(1).Locator(".mud-chip")).ToHaveTextAsync("Alice");
        await Expect(rows.Nth(1).Locator($"[data-testid='audio-play-{anchorId}']")).ToBeVisibleAsync();
    }
}
