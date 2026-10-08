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
}
