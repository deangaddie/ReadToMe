using System.Text.Json;
using Microsoft.Playwright;
using Read2Me.E2eTests.Infrastructure;

namespace Read2Me.E2eTests.Tests.Web;

/// <summary>
/// The narrator is one pinned entry in the speaker picker, and giving an item to it or taking an
/// item away from it flips what the row shows and what the item counts as (ADR-0006): the chip,
/// the host's item type, and whether audio mode lets the item be picked for generation.
/// </summary>
[Collection(E2eCollection.Name)]
public class NarratorPickerTests(E2eAppFixture app, PlaywrightFixture pw) : E2eTestBase(app, pw)
{
    private const string Folder = "web-narrator-picker";

    [Fact]
    public async Task Items_flip_between_the_narrator_and_a_character_through_the_picker()
    {
        var book = await App.SeedThreeDialogParagraphProjectAsync(
            Folder, "Web Narrator Picker Book", "A. Author", characterName: "Alice");
        var narrationId = book.ItemId("n1");
        var lineId = book.ItemId("line1");

        await GotoAppAsync($"projects/{Folder}/book?mode=speakers");
        var narration = Page.Locator("r2m-item .r2m-speaker-chip--narration");
        await Expect(narration).ToHaveCountAsync(1);

        // The narration item goes to Alice.
        await PickAsync(narrationId, Page.Locator(".r2m-speaker-menu__row", new() { HasText = "Alice" }));
        await Expect(Item(narrationId).Locator(".r2m-speaker-chip--named")).ToHaveTextAsync("Alice");
        await Expect(narration).ToHaveCountAsync(0);

        // An unattributed line goes to the narrator, through its pinned entry.
        await PickAsync(lineId, NarratorEntry);
        await Expect(Item(lineId).Locator(".r2m-speaker-chip--narration")).ToHaveCountAsync(1);

        // Both now have somebody to read them, so audio mode lets both be picked; the two lines
        // still unattributed stay off.
        await Page.GotoAsync(AppPath($"projects/{Folder}/book?mode=audio"));
        await Expect(Item(lineId).Locator("input[type=checkbox]")).ToBeEnabledAsync();
        await Expect(Item(narrationId).Locator("input[type=checkbox]")).ToBeEnabledAsync();
        await Expect(Page.Locator("r2m-item input[type=checkbox]:disabled")).ToHaveCountAsync(2);

        // The first item goes back to the narrator.
        await Page.GotoAsync(AppPath($"projects/{Folder}/book?mode=speakers"));
        await PickAsync(narrationId, NarratorEntry);
        await Expect(narration).ToHaveCountAsync(2);

        // Only a fresh read proves the flips reached the host: both are narration now, by type too.
        await Page.ReloadAsync();
        await Expect(narration).ToHaveCountAsync(2);
        await Expect(Page.Locator("r2m-item .r2m-speaker-chip--named")).ToHaveCountAsync(0);
        var types = await ItemTypesAsync(book.ChapterId("ch1"));
        Assert.Equal("Narration", types[narrationId]);
        Assert.Equal("Narration", types[lineId]);
    }

    private ILocator Item(Guid itemId) => Page.Locator($"r2m-item[data-item-id='{itemId}']");

    private ILocator NarratorEntry => Page.Locator(".r2m-speaker-menu__row",
        new() { Has = Page.Locator(".r2m-speaker-menu__narrator") });

    private async Task PickAsync(Guid itemId, ILocator entry)
    {
        await Item(itemId).Locator("r2m-speaker-chip button").ClickAsync();
        await entry.ClickAsync();
    }

    private async Task<Dictionary<Guid, string>> ItemTypesAsync(Guid chapterId)
    {
        var response = await Page.APIRequest.GetAsync(
            $"{App.BaseUrl}/api/projects/{Folder}/nodes/chapter/{chapterId}/children");
        using var doc = JsonDocument.Parse(await response.TextAsync());
        return doc.RootElement.GetProperty("paragraphs").EnumerateArray()
            .SelectMany(p => p.GetProperty("items").EnumerateArray())
            .ToDictionary(i => i.GetProperty("id").GetGuid(), i => i.GetProperty("itemType").GetString()!);
    }
}
