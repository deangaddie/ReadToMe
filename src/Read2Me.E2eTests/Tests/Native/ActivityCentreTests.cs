using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using Read2Me.App.Live;
using Read2Me.E2eTests.Infrastructure;
using Read2Me.E2eTests.Infrastructure.FakeAi;

namespace Read2Me.E2eTests.Tests.Native;

/// <summary>
/// The native activity centre (native-web 21) over a real attribution run: the pill in the activity
/// bar, the drawer's LLM stream joined only while its tab is open, the throughput table after the
/// run, and cancel from the pill. The fake LLM is slowed so the queue is observable while busy.
/// The run is queued from the reader's own "Attribute" action (native-web 25); the assertions are
/// the Angular class's. Runs in Chromium and Firefox.
/// </summary>
[Collection(E2eCollection.Name)]
public class ActivityCentreTests(E2eAppFixture app, PlaywrightFixture pw) : E2eTestBase(app, pw)
{
    protected override WebApp WebApp => WebApp.Native;

    private LiveConnectionRegistry Registry => App.Services.GetRequiredService<LiveConnectionRegistry>();

    [Fact]
    public async Task Pill_streams_only_while_the_llm_tab_is_open_and_table_after_the_run()
    {
        await App.SeedThreeDialogParagraphProjectAsync("native-activity", "Native Activity Book", "A. Author", characterName: "Alice");
        App.FakeAi.LlmReply = p => FakeAiResponses.AttributionReply(p, "Alice");
        App.FakeAi.LlmDelay = TimeSpan.FromSeconds(2);

        await GotoAppAsync("projects/native-activity/book");
        await Expect(Page.Locator("r2m-activity-bar")).ToContainTextAsync("No background work");

        await AttributeChapterAsync();

        // The pill appears with live counts; the ETA follows once the first paragraph has timed.
        var pill = Page.Locator("r2m-activity-bar .r2m-job-pill");
        await Expect(pill).ToContainTextAsync("Attributing");
        await Expect(pill).ToContainTextAsync("queued");
        await Expect(pill).ToContainTextAsync("ETA", new() { Timeout = 10_000 });

        // Nothing streams until the LLM tab is opened.
        Assert.Equal(0, Registry.StreamMemberCount(LiveGroups.StreamLlm));
        await pill.Locator(".r2m-job-pill__main").ClickAsync();
        var drawer = Page.Locator("r2m-activity-drawer");
        await Expect(drawer.Locator(".activity-jobs .r2m-job-card")).ToContainTextAsync("Attributing");
        await drawer.GetByRole(AriaRole.Tab, new() { Name = "LLM", Exact = true }).ClickAsync();
        await Expect(Page.Locator("r2m-llm-stream-tab .r2m-stream-llm__turn").First).ToBeVisibleAsync(new() { Timeout = 10_000 });
        await WaitForMembersAsync(LiveGroups.StreamLlm, 1);

        // Closing the drawer leaves the stream group (server member count drops).
        await Page.GetByLabel("Close activity drawer").ClickAsync();
        await WaitForMembersAsync(LiveGroups.StreamLlm, 0);

        // After the run: pill gone, per-config throughput table in the Jobs tab, Dismiss clears it.
        await App.WaitForQueueDrainAsync("/api/attribution/queue");
        await Expect(pill).ToHaveCountAsync(0);
        await Page.GetByLabel("Toggle activity drawer").ClickAsync();
        await drawer.GetByRole(AriaRole.Tab, new() { Name = "Jobs", Exact = true }).ClickAsync();
        var table = Page.Locator(".activity-jobs .r2m-throughput__table");
        await Expect(table).ToBeVisibleAsync(new() { Timeout = 10_000 });
        await Expect(table.Locator("tbody tr")).ToHaveCountAsync(1);
        await Page.Locator(".activity-jobs button", new() { HasText = "Dismiss" }).ClickAsync();
        await Expect(table).ToHaveCountAsync(0);
        await Expect(Page.Locator(".activity-jobs")).ToContainTextAsync("No background work");
    }

    [Fact]
    public async Task Cancel_from_the_pill_stops_the_queue()
    {
        await App.SeedThreeDialogParagraphProjectAsync("native-activity-cancel", "Native Cancel Book", "A. Author", characterName: "Alice");
        App.FakeAi.LlmReply = p => FakeAiResponses.AttributionReply(p, "Alice");
        App.FakeAi.LlmDelay = TimeSpan.FromSeconds(3);

        await GotoAppAsync("projects/native-activity-cancel/book?mode=speakers");
        await AttributeChapterAsync();

        var pill = Page.Locator("r2m-activity-bar .r2m-job-pill");
        await Expect(pill).ToContainTextAsync("queued");
        await pill.Locator(".r2m-job-pill__cancel").ClickAsync();

        // Queued work is dropped; only the paragraph already in flight can still resolve.
        await Expect(pill).ToHaveCountAsync(0, new() { Timeout = 10_000 });
        await App.WaitForQueueDrainAsync("/api/attribution/queue");
        var unknown = await Page.Locator(".r2m-item .r2m-speaker-chip--unknown").CountAsync();
        Assert.True(unknown >= 2, $"expected at least 2 paragraphs left unattributed after cancel, got {unknown}");
    }

    /// <summary>Ticks the chapter in the tree, which selects its three dialog paragraphs, then queues them.</summary>
    private async Task AttributeChapterAsync()
    {
        await Page.Locator("[data-node-id] .tree__select").First.CheckAsync();
        await Expect(Page.Locator("[data-testid='selection-count']")).ToHaveTextAsync("3 paragraphs");
        await Page.Locator("[data-action='attribute-selection']").ClickAsync();
    }

    private async Task WaitForMembersAsync(string group, int expected)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (Registry.StreamMemberCount(group) != expected && DateTime.UtcNow < deadline)
            await Task.Delay(100);
        Assert.Equal(expected, Registry.StreamMemberCount(group));
    }
}
