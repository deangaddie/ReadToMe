using System.Text.Json;
using Microsoft.Playwright;
using Read2Me.E2eTests.Infrastructure;
using Read2Me.TestUtils;

namespace Read2Me.E2eTests.Tests.Web;

/// <summary>
/// Hand-editing an item's delivery cue from the reader (delivery-cue-editing 03): the item menu's
/// Add / Edit / Remove entries, the text prompt with its provider hint, clicking a shown cue to
/// edit it, and <c>SetItemDeliveryCue</c> reloading the rows from its receipt — here and in a
/// second open reader. An item without audio is edited straight away; one with audio asks first,
/// and confirming discards its take (ticket 04). Runs in Chromium and Firefox.
/// </summary>
[Collection(E2eCollection.Name)]
public class DeliveryCueTests(E2eAppFixture app, PlaywrightFixture pw) : E2eTestBase(app, pw)
{
    private const string Hint = "Followed by Breeze; VoxCPM2 only loosely; ignored by Chatterbox and Qwen3.";

    [Fact]
    public async Task Add_click_edit_and_remove_a_cue_in_Speakers_mode()
    {
        const string folder = "native-cue-edit";
        var book = await App.SeedProjectAsync(folder, "Native Cue Edit Book", "A. Author");
        var itemId = book.ItemId("line1");

        await GotoAppAsync($"projects/{folder}/book?mode=speakers");
        var row = Page.Locator($"[data-item-id='{itemId}']");
        var cue = row.Locator("[data-testid=delivery-cue]");
        await Expect(row).ToHaveCountAsync(1);
        await Expect(cue).ToHaveCountAsync(0);

        // A second reader on the same project, which only ever watches.
        await using var other = await Page.Context.Browser!.NewContextAsync(new() { BaseURL = App.BaseUrl });
        var otherPage = await other.NewPageAsync();
        await otherPage.GotoAsync(AppPath($"projects/{folder}/book?mode=speakers"));
        var otherCue = otherPage.Locator($"[data-item-id='{itemId}'] [data-testid=delivery-cue]");
        await Expect(otherPage.Locator($"[data-item-id='{itemId}']")).ToHaveCountAsync(1);

        // Add: the menu offers only Add on an item without a cue; the prompt is empty, optional, hinted.
        await OpenItemMenuAsync(row);
        await Expect(Page.Locator("[role='menu'] [data-entry='edit-cue']")).ToHaveCountAsync(0);
        await Expect(Page.Locator("[role='menu'] [data-entry='remove-cue']")).ToHaveCountAsync(0);
        await Page.Locator("[role='menu'] [data-entry='add-cue']").ClickAsync();
        var prompt = Page.Locator("r2m-text-prompt-dialog");
        var input = prompt.Locator(".r2m-text-prompt-dialog__input");
        await Expect(prompt.Locator(".r2m-field__hint")).ToHaveTextAsync(Hint);
        await Expect(input).ToHaveValueAsync("");
        await Expect(prompt.Locator(".r2m-text-prompt-dialog__confirm")).ToBeEnabledAsync();
        await input.FillAsync("  whispered ");
        await prompt.Locator(".r2m-text-prompt-dialog__confirm").ClickAsync();

        await Expect(cue).ToHaveTextAsync("whispered");
        await Expect(otherCue).ToHaveTextAsync("whispered");

        // Click the cue: the same prompt, pre-filled; the edit replaces it everywhere.
        await cue.ClickAsync();
        await Expect(input).ToHaveValueAsync("whispered");
        await input.FillAsync("shouting across the hall");
        await prompt.Locator(".r2m-text-prompt-dialog__confirm").ClickAsync();
        await Expect(cue).ToHaveTextAsync("shouting across the hall");
        await Expect(otherCue).ToHaveTextAsync("shouting across the hall");

        // Too long: the host's refusal is an error toast and the cue stays.
        await cue.ClickAsync();
        await input.FillAsync(new string('x', 201));
        await prompt.Locator(".r2m-text-prompt-dialog__confirm").ClickAsync();
        await Expect(Page.Locator(".r2m-toast--error")).ToContainTextAsync("at most 200 characters");
        await Expect(cue).ToHaveTextAsync("shouting across the hall");

        // Remove: an item with a cue offers Edit and Remove, not Add.
        await OpenItemMenuAsync(row);
        await Expect(Page.Locator("[role='menu'] [data-entry='add-cue']")).ToHaveCountAsync(0);
        await Expect(Page.Locator("[role='menu'] [data-entry='edit-cue']")).ToHaveCountAsync(1);
        await Page.Locator("[role='menu'] [data-entry='remove-cue']").ClickAsync();
        await Expect(cue).ToHaveCountAsync(0);
        await Expect(otherCue).ToHaveCountAsync(0);

        var item = await LineItemAsync(folder, book, itemId);
        Assert.Equal(JsonValueKind.Null, item.GetProperty("voiceInstructions").ValueKind);
    }

    [Fact]
    public async Task A_cue_edit_on_an_item_with_audio_asks_first_and_discards_the_audio()
    {
        const string folder = "native-cue-audio";
        var book = await App.SeedProjectAsync(folder, "Native Cue Audio Book", "A. Author");
        var itemId = book.ItemId("line1");
        await App.SeedItemAudioAsync(folder, itemId, book.CharacterId("Alice"));

        await GotoAppAsync($"projects/{folder}/book?mode=audio");
        var row = Page.Locator($"[data-item-id='{itemId}']");
        var cue = row.Locator("[data-testid=delivery-cue]");
        var player = row.Locator("r2m-audio-player");
        var prompt = Page.Locator("r2m-text-prompt-dialog");
        var input = prompt.Locator(".r2m-text-prompt-dialog__input");
        var confirm = Page.Locator("r2m-confirm-dialog");
        await Expect(player).ToHaveCountAsync(1);

        // Cancel: the confirm names what is lost, and nothing changes.
        await OpenItemMenuAsync(row);
        await Page.Locator("[role='menu'] [data-entry='add-cue']").ClickAsync();
        await input.FillAsync("whispered");
        await prompt.Locator(".r2m-text-prompt-dialog__confirm").ClickAsync();
        await Expect(confirm.Locator(".r2m-confirm-dialog__message"))
            .ToHaveTextAsync("Adding a delivery cue discards this item's audio.");
        await confirm.Locator(".r2m-confirm-dialog__cancel").ClickAsync();
        await Expect(confirm).ToHaveCountAsync(0);
        await Expect(cue).ToHaveCountAsync(0);
        await Expect(player).ToHaveCountAsync(1);

        // Confirm: the cue lands and the take is gone.
        await OpenItemMenuAsync(row);
        await Page.Locator("[role='menu'] [data-entry='add-cue']").ClickAsync();
        await input.FillAsync("whispered");
        await prompt.Locator(".r2m-text-prompt-dialog__confirm").ClickAsync();
        await confirm.Locator(".r2m-confirm-dialog__confirm").ClickAsync();
        await Expect(cue).ToHaveTextAsync("whispered");
        await Expect(player).ToHaveCountAsync(0);
        Assert.Null(await AudioFileNameAsync(folder, book, itemId));

        // A fresh take, then re-saving the cue unchanged: no question, and the take stays.
        await App.SeedItemAudioAsync(folder, itemId, book.CharacterId("Alice"));
        await Page.ReloadAsync();
        await Expect(player).ToHaveCountAsync(1);
        await cue.ClickAsync();
        await Expect(input).ToHaveValueAsync("whispered");
        await prompt.Locator(".r2m-text-prompt-dialog__confirm").ClickAsync();
        await Expect(prompt).ToHaveCountAsync(0);
        await Expect(confirm).ToHaveCountAsync(0);
        await Expect(player).ToHaveCountAsync(1);
        Assert.Equal($"audio/{itemId}.wav", await AudioFileNameAsync(folder, book, itemId));

        // Remove on the voiced item asks too, and confirming discards the take again.
        await OpenItemMenuAsync(row);
        await Page.Locator("[role='menu'] [data-entry='remove-cue']").ClickAsync();
        await Expect(confirm.Locator(".r2m-confirm-dialog__message"))
            .ToHaveTextAsync("Removing the delivery cue discards this item's audio.");
        await confirm.Locator(".r2m-confirm-dialog__confirm").ClickAsync();
        await Expect(cue).ToHaveCountAsync(0);
        await Expect(player).ToHaveCountAsync(0);
        Assert.Null(await AudioFileNameAsync(folder, book, itemId));
    }

    private async Task<string?> AudioFileNameAsync(string folder, BookHierarchyBuilder book, Guid itemId) =>
        (await LineItemAsync(folder, book, itemId)).GetProperty("audioFileName").GetString();

    /// <summary>The seeded "line1" item as the host reports it (the chapter's second paragraph).</summary>
    private async Task<JsonElement> LineItemAsync(string folder, BookHierarchyBuilder book, Guid itemId)
    {
        var children = await Page.APIRequest.GetAsync(
            $"{App.BaseUrl}/api/projects/{folder}/nodes/chapter/{book.ChapterId("ch1")}/children");
        Assert.True(children.Ok, await children.TextAsync());
        using var doc = JsonDocument.Parse(await children.TextAsync());
        var item = doc.RootElement.GetProperty("paragraphs")[1].GetProperty("items")[0].Clone();
        Assert.Equal(itemId.ToString(), item.GetProperty("id").GetString());
        return item;
    }

    private async Task OpenItemMenuAsync(ILocator row)
    {
        var menu = row.Locator(".r2m-item__menu");
        await menu.HoverAsync();
        await menu.Locator("button").ClickAsync();
    }
}
