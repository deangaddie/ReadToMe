using Microsoft.Playwright;
using Read2Me.E2eTests.Infrastructure;

namespace Read2Me.E2eTests.Tests;

/// <summary>
/// The narrator link end to end: set it on the Characters tab, see it in the character list and in
/// the SplitAudio resolved-voice preview, unlink, see both revert. The unit suites own each site;
/// what only the real app proves is that the link written by the banner is the link the book tree's
/// preview reads back — two presenters, two tabs, one column.
/// <para>
/// The link is set through the UI rather than seeded: the write path is half of what is under test.
/// </para>
/// </summary>
[Collection(E2eCollection.Name)]
public class NarratorLinkTests(E2eAppFixture app, PlaywrightFixture pw) : E2eTestBase(app, pw)
{
    private const string Folder = "narrator-link";

    [Fact]
    public async Task Link_labels_the_narrator_row_and_the_voice_preview_until_it_is_unlinked()
    {
        var builder = await App.SeedProjectAsync(Folder, "Narrator Link", "A. Author", characterName: "Dr. Watson");
        // Both sides need a voice: Watson's is what narration resolves to while linked, and the
        // Narrator's is what must still be there after unlink (dormant, never deleted).
        await App.SeedEditableVoiceAsync(Folder, builder.CharacterId("Dr. Watson"), "Watson Voice");
        await App.SeedNarratorVoiceAsync(Folder);
        var narrationItemId = builder.ItemId("n1");

        // 1. Unlinked: the banner is an invitation and the seed row is plain "Narrator".
        await GotoAsync($"/project/{Folder}");
        await OpenCharactersTabAsync();

        var banner = Page.Locator("[data-testid='narrator-link-banner']");
        await Expect(banner).ToContainTextAsync("First-person book? Say who tells it");
        await Expect(NarratorRow).ToHaveCountAsync(1);
        await Expect(NarratorRow).Not.ToContainTextAsync("→");

        // 2. Set the link from the banner's picker — the only entry point there is.
        await PickNarratorAsync("Dr. Watson");
        await Expect(banner).ToContainTextAsync("Narrated by Dr. Watson");
        await Expect(banner).ToContainTextAsync("1 ready voice");
        await Expect(NarratorRow).ToContainTextAsync("Narrator → Dr. Watson");

        // 3. The book tree's preview reads the same link back — narration is labelled with the
        //    arrow, which is the one place display does not follow resolution.
        await OpenSplitAudioAsync();
        await Expect(VoicePreview(narrationItemId)).ToContainTextAsync("Narrator → Dr. Watson");
        await Expect(VoicePreview(narrationItemId)).ToContainTextAsync("Voice: Watson Voice");

        // 4. Unlink, warning confirmed.
        await OpenCharactersTabAsync();
        await banner.GetByRole(AriaRole.Button, new() { Name = "Unlink" }).ClickAsync();
        var dialog = Page.Locator(".mud-dialog");
        await Expect(dialog).ToContainTextAsync("Unlink Dr. Watson as this book's narrator?");
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Unlink" }).ClickAsync();

        await Expect(banner).ToContainTextAsync("First-person book? Say who tells it");
        // Count first: a negated assertion on a locator that matched nothing would pass on its own.
        await Expect(NarratorRow).ToHaveCountAsync(1);
        await Expect(NarratorRow).Not.ToContainTextAsync("→");

        // 5. The Narrator's own voice and rule woke up unchanged — its detail pane is an editor
        //    again, not the signpost, and the rule row is the positive proof the pane rendered.
        await NarratorRow.ClickAsync();
        await Expect(Page.Locator("[data-testid='voice-rule-row']")).ToHaveCountAsync(1);
        await Expect(Page.Locator("[data-testid='narrator-signpost']")).ToHaveCountAsync(0);
        await Expect(Page.GetByText("Narrator Voice").First).ToBeVisibleAsync();

        // 6. And the preview is back to the unlinked string. Reloaded, so the assertion is against
        //    the database rather than a presenter that happened to keep the new value.
        await GotoAsync($"/project/{Folder}");
        await OpenSplitAudioAsync();
        await Expect(VoicePreview(narrationItemId)).ToContainTextAsync("Voice: Narrator Voice");
        await Expect(VoicePreview(narrationItemId)).Not.ToContainTextAsync("→");
    }

    /// <summary>The seed Narrator row in the character list (the linked character's row also carries a book icon).</summary>
    private ILocator NarratorRow => Page.Locator(".mud-list-item")
        .Filter(new() { Has = Page.Locator("[data-book-icon='true']") })
        .Filter(new() { HasText = "Narrator" });

    private ILocator VoicePreview(Guid itemId) => Page.Locator($"[data-testid='voice-preview-{itemId}']");

    private async Task OpenCharactersTabAsync() =>
        await Page.GetByRole(AriaRole.Tab, new() { Name = "Characters" }).ClickAsync();

    /// <summary>Opens the banner's "Narrated by" select and picks a character.</summary>
    private async Task PickNarratorAsync(string characterName)
    {
        await Page.Locator("[data-testid='narrator-link-banner'] .mud-select").First.ClickAsync();
        await Page.Locator(".mud-popover .mud-list-item", new() { HasText = characterName }).ClickAsync();
    }

    /// <summary>Book tab → Split: Audio (which re-resolves the preview) → ch1 expanded.</summary>
    private async Task OpenSplitAudioAsync()
    {
        await Page.GetByRole(AriaRole.Tab, new() { Name = "Book" }).ClickAsync();
        await Page.GetByText("Split: Audio").ClickAsync();
        await Page.GetByText("ch1").ClickAsync();
        // The expansion panel animates open; assert against a settled tree.
        await Expect(Page.Locator(".mud-collapse-entering")).ToHaveCountAsync(0);
    }
}
