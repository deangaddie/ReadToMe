using System.Text.Json;
using Microsoft.Playwright;
using Read2Me.E2eTests.Infrastructure;

namespace Read2Me.E2eTests.Tests.Web;

/// <summary>
/// Hand-editing an item's delivery cue from the reader (delivery-cue-editing 03): the item menu's
/// Add / Edit / Remove entries, the text prompt with its provider hint, clicking a shown cue to
/// edit it, and <c>SetItemDeliveryCue</c> reloading the rows from its receipt — here and in a
/// second open reader. The item has no audio, so no confirm is asked (ticket 04). Runs in
/// Chromium and Firefox.
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

        var children = await Page.APIRequest.GetAsync(
            $"{App.BaseUrl}/api/projects/{folder}/nodes/chapter/{book.ChapterId("ch1")}/children");
        Assert.True(children.Ok, await children.TextAsync());
        using var doc = JsonDocument.Parse(await children.TextAsync());
        var item = doc.RootElement.GetProperty("paragraphs")[1].GetProperty("items")[0];
        Assert.Equal(itemId.ToString(), item.GetProperty("id").GetString());
        Assert.Equal(JsonValueKind.Null, item.GetProperty("voiceInstructions").ValueKind);
    }

    private async Task OpenItemMenuAsync(ILocator row)
    {
        var menu = row.Locator(".r2m-item__menu");
        await menu.HoverAsync();
        await menu.Locator("button").ClickAsync();
    }
}
