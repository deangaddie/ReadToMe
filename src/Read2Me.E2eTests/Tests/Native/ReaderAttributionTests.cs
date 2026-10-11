using Microsoft.Playwright;
using Read2Me.E2eTests.Infrastructure;
using Read2Me.E2eTests.Infrastructure.FakeAi;

namespace Read2Me.E2eTests.Tests.Native;

/// <summary>
/// The native reader's attribution loop end to end (native-web 25, from the Angular class of the
/// same name): select paragraphs from the tree and the rows, queue them, watch the rows resolve
/// from receipts without a reload, then assign, bulk-assign and clear speakers by hand. Runs in
/// Chromium and Firefox.
/// </summary>
[Collection(E2eCollection.Name)]
public class ReaderAttributionTests(E2eAppFixture app, PlaywrightFixture pw) : E2eTestBase(app, pw)
{
    protected override WebApp WebApp => WebApp.Native;

    [Fact]
    public async Task Select_unprocessed_attribute_and_watch_the_rows_resolve()
    {
        await App.SeedThreeDialogParagraphProjectAsync("native-attr", "Native Attr Book", "A. Author", characterName: "Alice");
        App.FakeAi.LlmReply = p => FakeAiResponses.AttributionReply(p, "Alice");

        await GotoAppAsync("projects/native-attr/book?mode=speakers");

        var unknown = Page.Locator(".r2m-item .r2m-speaker-chip--unknown");
        await Expect(unknown).ToHaveCountAsync(3);

        // The chapter's tree checkbox selects every Character paragraph under it.
        await Page.Locator("[data-node-id] .tree__select").First.CheckAsync();
        await Expect(Page.Locator("[data-testid='selection-count']")).ToHaveTextAsync("3 paragraphs");
        await Expect(Page.Locator(".r2m-paragraph input[type=checkbox]:checked")).ToHaveCountAsync(3);

        await Page.Locator("[data-action='attribute-selection']").ClickAsync();

        // The selection is let go, and the fake LLM's answers arrive as receipts.
        await Expect(Page.Locator("[data-testid='selection-bar']")).ToHaveCountAsync(0);
        await Expect(Page.Locator(".r2m-item .r2m-speaker-chip--named", new() { HasText = "Alice" }))
            .ToHaveCountAsync(3, new() { Timeout = 20_000 });
        await Expect(unknown).ToHaveCountAsync(0);
        await Expect(Page.Locator(".r2m-toast", new() { HasText = "Book updated elsewhere" })).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task Assign_clear_and_bulk_assign_speakers_from_the_chips()
    {
        await App.SeedThreeDialogParagraphProjectAsync("native-assign", "Native Assign Book", "A. Author", characterName: "Alice");

        await GotoAppAsync("projects/native-assign/book?mode=speakers");

        var items = Page.Locator(".r2m-item");
        await Expect(items).ToHaveCountAsync(4);

        // One item: pick Alice from its chip.
        await items.Nth(1).Locator("r2m-speaker-chip button").ClickAsync();
        await Page.Locator(".r2m-speaker-menu__row", new() { HasText = "Alice" }).ClickAsync();
        await Expect(items.Nth(1).Locator(".r2m-speaker-chip--named")).ToHaveTextAsync("Alice");

        // Clear it again.
        await items.Nth(1).Locator("r2m-speaker-chip button").ClickAsync();
        await Page.Locator(".r2m-speaker-menu__action", new() { HasText = "Clear speaker" }).ClickAsync();
        await Expect(items.Nth(1).Locator(".r2m-speaker-chip--unknown")).ToHaveCountAsync(1);

        // New character by keyboard alone: the search box has focus on open, and Enter on a name
        // nobody matches creates it, assigned in the same gesture.
        await items.Nth(2).Locator("r2m-speaker-chip button").ClickAsync();
        await Expect(Page.Locator(".r2m-speaker-menu__input")).ToBeFocusedAsync();
        await Page.Keyboard.TypeAsync("Bob");
        await Page.Keyboard.PressAsync("Enter");
        await Expect(items.Nth(2).Locator(".r2m-speaker-chip--named")).ToHaveTextAsync("Bob");
        await Expect(Page.Locator(".r2m-speaker-menu")).ToHaveCountAsync(0);

        // Bulk: the two rows around Bob's ticked, Alice picked once, the confirm quotes the preview.
        var boxes = Page.Locator(".r2m-paragraph input[type=checkbox]");
        await Expect(boxes).ToHaveCountAsync(3);
        await boxes.Nth(0).CheckAsync();
        await boxes.Nth(2).CheckAsync();
        await Expect(Page.Locator("[data-testid='selection-count']")).ToHaveTextAsync("2 paragraphs");
        await Page.Locator("[data-action='bulk-assign']").ClickAsync();
        await Page.Locator(".r2m-speaker-menu__row", new() { HasText = "Alice" }).ClickAsync();
        var confirm = Page.Locator("r2m-confirm-dialog");
        await Expect(confirm).ToContainTextAsync("Alice becomes the speaker for 2 dialog lines in 2 paragraphs");
        await confirm.Locator(".r2m-confirm-dialog__confirm").ClickAsync();

        await Expect(Page.Locator(".r2m-item .r2m-speaker-chip--named", new() { HasText = "Alice" })).ToHaveCountAsync(2);
        await Expect(Page.Locator(".r2m-item .r2m-speaker-chip--named", new() { HasText = "Bob" })).ToHaveCountAsync(1);

        // The host agrees: three stamped dialog items, none left unattributed.
        var characters = await Page.APIRequest.GetAsync($"{App.BaseUrl}/api/projects/native-assign/characters");
        Assert.Contains("\"Bob\"", await characters.TextAsync());
        var status = await Page.APIRequest.GetAsync($"{App.BaseUrl}/api/projects/native-assign/status");
        Assert.Contains("\"unattributed\":0", await status.TextAsync());
    }

    [Fact]
    public async Task Release_a_transcript_chapter_then_bulk_assign_its_answers()
    {
        var book = await App.SeedTranscriptProjectAsync("native-release", "Native Release Book", "A. Author", characterName: "Alice");
        var chapter = book.ChapterId("ch1");
        ILocator Row(string paragraph) => Page.Locator($"[data-paragraph-id='{book.ParagraphId(paragraph)}']");
        ILocator Chip(string item, string state) =>
            Page.Locator($"[data-item-id='{book.ItemId(item)}'] .r2m-speaker-chip--{state}");

        await GotoAppAsync("projects/native-release/book?mode=speakers");

        // Before: the transcript is narration, so only the quoted paragraph can be selected.
        var boxes = Page.Locator(".r2m-paragraph input[type=checkbox]");
        await Expect(boxes).ToHaveCountAsync(1);
        await Expect(Chip("a1", "narration")).ToHaveCountAsync(1);
        await Expect(Chip("tag", "narration")).ToHaveCountAsync(1);
        var node = Page.Locator($"[data-node-id='{chapter}']");
        await Expect(node.Locator(".r2m-count-badge--attribution .r2m-count-badge__count")).ToHaveTextAsync("1");

        // Release the chapter from its menu; the confirm quotes the preview.
        await node.HoverAsync();
        await node.Locator(".r2m-node-menu__trigger").ClickAsync();
        await Page.Locator("[role=menu] [data-entry='release-narration']").ClickAsync();
        var confirm = Page.Locator("r2m-confirm-dialog");
        await Expect(confirm).ToContainTextAsync("Release 10 narration paragraphs in");
        await Expect(confirm).Not.ToContainTextAsync("discarded");
        await confirm.Locator(".r2m-confirm-dialog__confirm").ClickAsync();

        // The receipt converges the page: Q/A rows are unattributed dialog with checkboxes, the
        // plain narration and the speech tag stay narration, and the badge counts the released
        // paragraphs.
        await Expect(boxes).ToHaveCountAsync(11);
        for (var n = 1; n <= 5; n++)
        {
            await Expect(Chip($"q{n}", "unknown")).ToHaveCountAsync(1);
            await Expect(Chip($"a{n}", "unknown")).ToHaveCountAsync(1);
        }
        await Expect(Chip("plain", "narration")).ToHaveCountAsync(1);
        await Expect(Row("pplain").Locator("input[type=checkbox]")).ToHaveCountAsync(0);
        await Expect(Chip("tag", "narration")).ToHaveCountAsync(1);
        await Expect(node.Locator(".r2m-count-badge--attribution .r2m-count-badge__count")).ToHaveTextAsync("11");

        // Bulk-assign Alice to the answers.
        await Row("pa1").Locator("input[type=checkbox]").CheckAsync();
        await Row("pa2").Locator("input[type=checkbox]").CheckAsync();
        await Expect(Page.Locator("[data-testid='selection-count']")).ToHaveTextAsync("2 paragraphs");
        await Page.Locator("[data-action='bulk-assign']").ClickAsync();
        await Page.Locator(".r2m-speaker-menu__row", new() { HasText = "Alice" }).ClickAsync();
        await Expect(confirm).ToContainTextAsync("Alice becomes the speaker for 2 dialog lines in 2 paragraphs");
        await confirm.Locator(".r2m-confirm-dialog__confirm").ClickAsync();

        await Expect(Chip("a1", "named")).ToHaveTextAsync("Alice");
        await Expect(Chip("a2", "named")).ToHaveTextAsync("Alice");
        await Expect(Chip("q1", "unknown")).ToHaveCountAsync(1);

        // The release queued nothing for attribution.
        var queue = await Page.APIRequest.GetAsync($"{App.BaseUrl}/api/attribution/queue");
        Assert.Contains("\"queuedCount\":0", await queue.TextAsync());
    }

    [Fact]
    public async Task Release_one_unquoted_paragraph_from_its_menu_without_a_confirm()
    {
        var book = await App.SeedTranscriptProjectAsync("native-release-one", "Native Release One", "A. Author");
        ILocator Row(string paragraph) => Page.Locator($"[data-paragraph-id='{book.ParagraphId(paragraph)}']");
        ILocator Chip(string item, string state) =>
            Page.Locator($"[data-item-id='{book.ItemId(item)}'] .r2m-speaker-chip--{state}");
        async Task OpenMenu(string paragraph)
        {
            await Row(paragraph).HoverAsync();
            await Row(paragraph).Locator(".r2m-paragraph__menu .r2m-node-menu__trigger").ClickAsync();
        }
        var release = Page.Locator("[role=menu] [data-entry='release-narration']");

        await GotoAppAsync("projects/native-release-one/book?mode=speakers");
        await Expect(Chip("q1", "narration")).ToHaveCountAsync(1);

        // The quoted paragraph holds dialog, so its menu offers no release.
        await OpenMenu("pquoted");
        await Expect(Page.Locator("[role=menu] [data-entry='delete']")).ToBeVisibleAsync();
        await Expect(release).ToHaveCountAsync(0);
        await Page.Keyboard.PressAsync("Escape");

        // A narration-only paragraph without audio is released straight away: no confirm.
        await OpenMenu("pq1");
        await release.ClickAsync();
        await Expect(Chip("q1", "unknown")).ToHaveCountAsync(1);
        await Expect(Row("pq1").Locator("input[type=checkbox]")).ToHaveCountAsync(1);
        await Expect(Page.Locator(".r2m-confirm-dialog__confirm")).ToBeHiddenAsync();

        // Only that paragraph: its neighbours and the speech tag stay narration.
        await Expect(Chip("a1", "narration")).ToHaveCountAsync(1);
        await Expect(Chip("tag", "narration")).ToHaveCountAsync(1);
        await Expect(Page.Locator(".r2m-paragraph input[type=checkbox]")).ToHaveCountAsync(2);
    }
}
