using System.Text.Json;
using Microsoft.Playwright;
using Read2Me.E2eTests.Infrastructure;

namespace Read2Me.E2eTests.Tests.Native;

/// <summary>
/// Inserting an item runs the whole edit chain from an item row (native-web 24, from the Angular
/// class of the same name): the item's node menu, the text prompt, <c>InsertParagraphItem</c>, and
/// the reader reloading from its receipt. The mis-split fixture is the repair the command exists
/// for: "mixed" holds two speakers, and the anchor is stamped with a speaker and audio so the test
/// can see the insertion leave that work alone. Audio mode shows both the item's selection
/// checkbox and its player, so "unattributed" and "the take still plays" are read off the rows
/// (native-web 26) as well as the host. Runs in Chromium and Firefox.
/// </summary>
[Collection(E2eCollection.Name)]
public class InsertItemTests(E2eAppFixture app, PlaywrightFixture pw) : E2eTestBase(app, pw)
{
    protected override WebApp WebApp => WebApp.Native;

    [Fact]
    public async Task Insert_item_after_adds_an_unattributed_row_and_leaves_the_anchor_alone()
    {
        const string folder = "native-insert";
        var book = await App.SeedMisSplitParagraphProjectAsync(
            folder, "Native Insert Book", "A. Author", characterName: "Alice");
        var anchorId = book.ItemId("mixed");
        await App.SeedItemAudioAsync(folder, anchorId, book.CharacterId("Alice"));

        // Audio mode: the item rows, each with its speaker chip and voice line.
        await GotoAppAsync($"projects/{folder}/book?mode=audio");

        var rows = Page.Locator($".r2m-paragraph[data-paragraph-id='{book.ParagraphId("p2")}'] .r2m-item");
        await Expect(rows).ToHaveCountAsync(3);

        var menu = rows.Nth(1).Locator(".r2m-item__menu");
        await menu.HoverAsync();
        await menu.Locator("button").ClickAsync();
        await Page.Locator("[role='menu'] [data-entry='insert-after']").ClickAsync();

        const string newText = "“And who might you be?” he answered.";
        var prompt = Page.Locator("r2m-text-prompt-dialog");
        await Expect(prompt).ToBeVisibleAsync();
        await prompt.Locator(".r2m-text-prompt-dialog__input").FillAsync(newText);
        await prompt.Locator(".r2m-text-prompt-dialog__confirm").ClickAsync();

        // The new row sits straight after the anchor and carries the typed text.
        await Expect(rows).ToHaveCountAsync(4);
        await Expect(rows.Nth(2)).ToContainTextAsync(newText);
        await Expect(rows.Nth(3)).ToContainTextAsync("“Only me,” came the reply.");

        // Born unattributed: the Unknown chip, and nobody can read it yet, so it cannot be picked for audio.
        await Expect(rows.Nth(2).Locator(".r2m-speaker-chip--unknown")).ToHaveCountAsync(1);
        await Expect(rows.Nth(2).Locator("input[type=checkbox]")).ToBeDisabledAsync();
        await Expect(Page.Locator(".r2m-toast")).ToHaveCountAsync(0);

        // The anchor is untouched: same text, same speaker, its take still plays, and the host agrees.
        await Expect(rows.Nth(1)).ToContainTextAsync(
            "“Hello there,” she said. “And who might you be?” he answered.");
        await Expect(rows.Nth(1).Locator(".r2m-speaker-chip--named")).ToHaveTextAsync("Alice");
        await Expect(rows.Nth(1).Locator("r2m-audio-player")).ToHaveCountAsync(1);

        var children = await Page.APIRequest.GetAsync(
            $"{App.BaseUrl}/api/projects/{folder}/nodes/chapter/{book.ChapterId("ch1")}/children");
        Assert.True(children.Ok, await children.TextAsync());
        using var doc = JsonDocument.Parse(await children.TextAsync());
        var items = doc.RootElement.GetProperty("paragraphs")[1].GetProperty("items");
        Assert.Equal(4, items.GetArrayLength());
        Assert.Equal($"audio/{anchorId}.wav", items[1].GetProperty("audioFileName").GetString());
        Assert.Equal(JsonValueKind.Null, items[2].GetProperty("audioFileName").ValueKind);
    }
}
