using Read2Me.E2eTests.Infrastructure;

namespace Read2Me.E2eTests.Tests.Web;

/// <summary>
/// Throwaway proof for #477: a state icon is an image named by its state, and no ligature text
/// reaches the accessibility tree. Deleted before merge.
/// </summary>
public class StateIconProofTests(E2eAppFixture app, PlaywrightFixture pw) : E2eTestBase(app, pw)
{
    private readonly string _engine = pw.IsFirefox ? "firefox" : "chromium";

    /// <summary>Keeps each snapshot for the PR body.</summary>
    private void Record(string what, string snapshot) =>
        File.AppendAllText(Path.Combine(Path.GetTempPath(), $"r2m-477-{_engine}.txt"), $"[{what}]\n{snapshot}\n");

    [Fact]
    public async Task A_done_chapter_and_a_linked_narrator_chip_are_named_images()
    {
        const string folder = "state-icons-book";
        var book = await App.SeedProjectAsync(folder, "Pride and Prejudice", "Jane Austen", characterName: "Elizabeth");
        var elizabeth = book.CharacterId("Elizabeth");
        // Every item voiced, the line spoken by Elizabeth, so the chapter is done.
        await App.SeedItemAudioAsync(folder, book.ItemId("n1"), Read2Me.Data.ProjectDbContext.NarratorId);
        await App.SeedItemAudioAsync(folder, book.ItemId("line1"), elizabeth);
        await App.SeedItemAudioAsync(folder, book.ItemId("n2"), Read2Me.Data.ProjectDbContext.NarratorId);
        var link = await Page.APIRequest.PostAsync($"{App.BaseUrl}/api/projects/{folder}/commands",
            new() { DataObject = new { type = "SetNarratorCharacter", characterId = elizabeth } });
        Assert.Equal(200, link.Status);

        await GotoAppAsync($"projects/{folder}/book?mode=speakers");

        var done = Page.Locator("[role=treeitem]:has(.tree__done)");
        await Expect(done).ToBeVisibleAsync();
        var tree = await done.AriaSnapshotAsync();
        Record("done chapter", tree);
        Assert.Contains("img \"Done\"", tree);
        Assert.DoesNotContain("check_circle", tree);

        var chip = Page.Locator("r2m-speaker-chip.r2m-speaker-chip--narrator-linked").First;
        await Expect(chip).ToBeVisibleAsync();
        var chipTree = await chip.AriaSnapshotAsync();
        Record("linked-narrator chip", chipTree);
        Assert.Contains("img \"Linked narrator\"", chipTree);
        Assert.DoesNotContain("link", chipTree.Replace("Linked narrator", ""));
    }

    [Fact]
    public async Task The_default_voice_star_is_a_named_image()
    {
        const string folder = "state-icons-cast";
        var book = await App.SeedProjectAsync(folder, "Pride and Prejudice", "Jane Austen", characterName: "Mary Bennet");
        var mary = book.CharacterId("Mary Bennet");
        await App.SeedEditableVoiceAsync(folder, mary, "Mary Bennet");

        await GotoAppAsync($"projects/{folder}/cast/{mary}");

        var card = Page.Locator("r2m-voice-card .voice-card__header");
        await Expect(card).ToBeVisibleAsync();
        var cardTree = await card.AriaSnapshotAsync();
        Record("default voice card header", cardTree);
        Assert.Contains("img \"Default voice\"", cardTree);
        Assert.DoesNotContain("star", cardTree);
    }
}
