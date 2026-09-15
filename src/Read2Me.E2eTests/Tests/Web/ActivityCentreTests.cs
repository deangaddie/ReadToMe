using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using Read2Me.App.Live;
using Read2Me.E2eTests.Infrastructure;
using Read2Me.E2eTests.Infrastructure.FakeAi;

namespace Read2Me.E2eTests.Tests.Web;

/// <summary>
/// The activity centre (Angular ticket 14) over a real attribution run: the pill in the activity
/// bar, the drawer's LLM stream joined only while its tab is open, the throughput table after the
/// run, and cancel from the pill. The fake LLM is slowed so the queue is observable while busy.
/// </summary>
[Collection(E2eCollection.Name)]
public class ActivityCentreTests(E2eAppFixture app, PlaywrightFixture pw) : WebE2eTestBase(app, pw)
{
    private LiveConnectionRegistry Registry => App.Services.GetRequiredService<LiveConnectionRegistry>();

    [Fact]
    public async Task Pill_streams_only_while_the_llm_tab_is_open_and_table_after_the_run()
    {
        await App.SeedThreeDialogParagraphProjectAsync("web-activity", "Web Activity Book", "A. Author", characterName: "Alice");
        App.FakeAi.LlmReply = p => FakeAiResponses.AttributionReply(p, "Alice");
        App.FakeAi.LlmDelay = TimeSpan.FromSeconds(2);

        await GotoAppAsync("/app/projects/web-activity/book?mode=speakers");
        await Expect(Page.Locator("app-activity-bar")).ToContainTextAsync("No background work");

        await Page.Locator("[data-node-id] .tree__select").First.CheckAsync();
        await Page.Locator("[data-action='attribute-selection']").ClickAsync();

        // The pill appears with live counts; the ETA follows once the first paragraph has timed.
        var pill = Page.Locator("app-activity-bar r2m-job-pill");
        await Expect(pill).ToContainTextAsync("Attributing");
        await Expect(pill).ToContainTextAsync("queued");
        await Expect(pill).ToContainTextAsync("ETA", new() { Timeout = 10_000 });

        // Nothing streams until the LLM tab is opened.
        Assert.Equal(0, Registry.StreamMemberCount(LiveGroups.StreamLlm));
        await pill.Locator(".r2m-job-pill__main").ClickAsync();
        await Expect(Page.Locator("app-activity-drawer app-jobs-tab r2m-job-card")).ToContainTextAsync("Attributing");
        await Page.Locator("a[mat-tab-link][data-tab='llm']").ClickAsync();
        await Expect(Page.Locator("app-llm-stream-tab .r2m-stream-llm__turn").First).ToBeVisibleAsync(new() { Timeout = 10_000 });
        await WaitForMembersAsync(LiveGroups.StreamLlm, 1);

        // Closing the drawer leaves the stream group (server member count drops).
        await Page.GetByLabel("Close activity drawer").ClickAsync();
        await WaitForMembersAsync(LiveGroups.StreamLlm, 0);

        // After the run: pill gone, per-config throughput table in the Jobs tab, Dismiss clears it.
        await App.WaitForQueueDrainAsync("/api/attribution/queue");
        await Expect(pill).ToHaveCountAsync(0);
        await Page.GetByLabel("Toggle activity drawer").ClickAsync();
        await Page.Locator("a[mat-tab-link][data-tab='jobs']").ClickAsync();
        var table = Page.Locator("app-jobs-tab .activity-jobs__table");
        await Expect(table).ToBeVisibleAsync(new() { Timeout = 10_000 });
        await Expect(table.Locator("tbody tr")).ToHaveCountAsync(1);
        await Page.Locator("app-jobs-tab button", new() { HasText = "Dismiss" }).ClickAsync();
        await Expect(table).ToHaveCountAsync(0);
        await Expect(Page.Locator("app-jobs-tab")).ToContainTextAsync("No background work");
    }

    [Fact]
    public async Task Cancel_from_the_pill_stops_the_queue()
    {
        await App.SeedThreeDialogParagraphProjectAsync("web-activity-cancel", "Web Cancel Book", "A. Author", characterName: "Alice");
        App.FakeAi.LlmReply = p => FakeAiResponses.AttributionReply(p, "Alice");
        App.FakeAi.LlmDelay = TimeSpan.FromSeconds(3);

        await GotoAppAsync("/app/projects/web-activity-cancel/book?mode=speakers");
        await Page.Locator("[data-node-id] .tree__select").First.CheckAsync();
        await Page.Locator("[data-action='attribute-selection']").ClickAsync();

        var pill = Page.Locator("app-activity-bar r2m-job-pill");
        await Expect(pill).ToContainTextAsync("queued");
        await pill.Locator(".r2m-job-pill__cancel").ClickAsync();

        // Queued work is dropped; only the paragraph already in flight can still resolve.
        await Expect(pill).ToHaveCountAsync(0, new() { Timeout = 10_000 });
        await App.WaitForQueueDrainAsync("/api/attribution/queue");
        var unknown = await Page.Locator("r2m-item .r2m-speaker-chip--unknown").CountAsync();
        Assert.True(unknown >= 2, $"expected at least 2 paragraphs left unattributed after cancel, got {unknown}");
    }

    private async Task WaitForMembersAsync(string group, int expected)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (Registry.StreamMemberCount(group) != expected && DateTime.UtcNow < deadline)
            await Task.Delay(100);
        Assert.Equal(expected, Registry.StreamMemberCount(group));
    }
}
