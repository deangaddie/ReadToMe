using Microsoft.Playwright;
using Read2Me.E2eTests.Infrastructure;

namespace Read2Me.E2eTests.Tests.Web;

/// <summary>
/// The web reader's audio loop end to end (Angular ticket 13): select what needs audio from the
/// tree menu, queue it, watch the items go Queued → Processing → playable from hub status and
/// receipts without a reload, retry a failed item, and dismiss a review flag that stays dismissed.
/// </summary>
[Collection(E2eCollection.Name)]
public class ReaderAudioTests(E2eAppFixture app, PlaywrightFixture pw) : WebE2eTestBase(app, pw)
{
    private static ILocator ItemRow(IPage page, Guid itemId) => page.Locator($"r2m-item[data-item-id='{itemId}']");

    [Fact]
    public async Task Select_needs_audio_generate_and_watch_the_items_become_playable()
    {
        var book = await App.SeedProjectAsync("web-audio", "Web Audio Book", "A. Author");
        await App.SeedNarratorVoiceAsync("web-audio");
        var chapterId = book.ChapterId("ch1");

        await GotoAppAsync("/app/projects/web-audio/book?mode=audio");

        // Three paragraphs miss audio; the unattributed line's checkbox is off, the narration ones live.
        var chapterBadge = Page.Locator($"[data-node-id='{chapterId}'] r2m-count-badge[kind='audio'] .r2m-count-badge__count");
        await Expect(chapterBadge).ToHaveTextAsync("3");
        await Expect(Page.Locator("r2m-item input[type=checkbox]:disabled")).ToHaveCountAsync(1);
        await Expect(Page.Locator("r2m-audio-player")).ToHaveCountAsync(0);

        // "Select needs audio" from the chapter's menu selects the two narration items.
        await Page.Locator($"[data-node-id='{chapterId}'] r2m-node-menu button").ClickAsync();
        await Page.Locator("[data-entry='select-needs-audio']").ClickAsync();
        await Expect(Page.Locator("[data-testid='selection-count']")).ToHaveTextAsync("2 items");
        await Expect(Page.Locator("r2m-item input[type=checkbox]:checked")).ToHaveCountAsync(2);

        await Page.Locator("[data-action='generate-audio-selection']").ClickAsync();

        // The selection is let go; the queue's status and the recorded takes arrive live.
        await Expect(Page.Locator("[data-testid='selection-bar']")).ToHaveCountAsync(0);
        await Expect(Page.Locator("r2m-audio-player")).ToHaveCountAsync(2, new() { Timeout = 30_000 });
        await Expect(Page.Locator("r2m-item r2m-status-chip", new() { HasText = "Queued" })).ToHaveCountAsync(0);
        await Expect(Page.Locator("r2m-item r2m-status-chip", new() { HasText = "Processing" })).ToHaveCountAsync(0);
        await Expect(chapterBadge).ToHaveTextAsync("1");
        await Expect(Page.Locator(".r2m-toast-panel", new() { HasText = "Book updated elsewhere" })).ToHaveCountAsync(0);

        // The player addresses the take through the workspace mount, cache-busted by its version.
        var src = await ItemRow(Page, book.ItemId("n1")).Locator("audio").GetAttributeAsync("src");
        Assert.NotNull(src);
        Assert.Contains($"/workspace/web-audio/audio/{book.ItemId("n1")}.wav?v=", src);
        Assert.True(File.Exists(Path.Combine(App.WorkspaceDir, "web-audio", "audio", $"{book.ItemId("n1")}.wav")));
    }

    [Fact]
    public async Task A_failed_item_shows_its_reason_and_Retry_requeues_just_that_item()
    {
        // No narrator voice yet: the narration item fails to resolve and records why.
        var book = await App.SeedProjectAsync("web-audio-retry", "Web Retry Book", "A. Author");
        var itemId = book.ItemId("n1");

        var enqueue = await Page.APIRequest.PostAsync(
            $"{App.BaseUrl}/api/projects/web-audio-retry/audio/enqueue-items",
            new() { DataObject = new { itemIds = new[] { itemId } } });
        Assert.Equal(202, enqueue.Status);
        await App.WaitForQueueDrainAsync("/api/audio/queue");

        await GotoAppAsync("/app/projects/web-audio-retry/book?mode=audio");

        var row = ItemRow(Page, itemId);
        var chip = row.Locator("r2m-status-chip", new() { HasText = "Failed" });
        await Expect(chip).ToBeVisibleAsync();
        await chip.HoverAsync();
        await Expect(Page.Locator(".mat-mdc-tooltip")).ToContainTextAsync("No default voice");
        await Expect(Page.Locator("[data-action='retry-audio']")).ToHaveCountAsync(1);

        // Give the narrator a voice, then retry only this item.
        await App.SeedNarratorVoiceAsync("web-audio-retry");
        await row.Locator("[data-action='retry-audio']").ClickAsync();

        await Expect(Page.Locator(".r2m-toast-panel", new() { HasText = "Queued 1 item" })).ToBeVisibleAsync();
        await Expect(row.Locator("r2m-audio-player")).ToHaveCountAsync(1, new() { Timeout = 30_000 });
        await Expect(row.Locator("r2m-status-chip")).ToHaveCountAsync(0);
        await Expect(Page.Locator("r2m-audio-player")).ToHaveCountAsync(1);
        Assert.False(File.Exists(Path.Combine(App.WorkspaceDir, "web-audio-retry", "audio", $"{book.ItemId("n2")}.wav")));
    }

    [Fact]
    public async Task A_review_flag_can_be_dismissed_and_stays_dismissed_after_reload()
    {
        var book = await App.SeedProjectAsync("web-audio-review", "Web Review Book", "A. Author");
        var itemId = book.ItemId("n1");

        var flag = await Page.APIRequest.PostAsync(
            $"{App.BaseUrl}/api/projects/web-audio-review/commands",
            new()
            {
                DataObject = new
                {
                    type = "SetAudioReview",
                    paragraphItemId = itemId,
                    normalizeOk = true,
                    verifyOk = false,
                    wer = 0.4,
                    verifyReason = "transcript drifted",
                },
            });
        Assert.Equal(200, flag.Status);

        await GotoAppAsync("/app/projects/web-audio-review/book?mode=audio");

        var row = ItemRow(Page, itemId);
        await Expect(row.Locator("r2m-status-chip", new() { HasText = "Verify failed" })).ToBeVisibleAsync();
        await row.Locator("[data-action='dismiss-review']").ClickAsync();

        await Expect(row.Locator("[data-testid='review-dismissed']")).ToBeVisibleAsync();
        await Expect(row.Locator("r2m-status-chip")).ToHaveCountAsync(0);

        await GotoAppAsync("/app/projects/web-audio-review/book?mode=audio");
        await Expect(ItemRow(Page, itemId).Locator("[data-testid='review-dismissed']")).ToBeVisibleAsync();
        await Expect(ItemRow(Page, itemId).Locator("r2m-status-chip")).ToHaveCountAsync(0);
    }
}
