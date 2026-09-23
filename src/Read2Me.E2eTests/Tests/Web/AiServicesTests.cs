using Microsoft.Playwright;
using Read2Me.E2eTests.Infrastructure;
using Read2Me.E2eTests.Infrastructure.FakeAi;
using Read2Me.Services.Health;

namespace Read2Me.E2eTests.Tests.Web;

/// <summary>
/// The services dashboard and the preflight sheet (Angular ticket 25) against the real host and the
/// fake container controller: a lifecycle op from the page flips the chip over the hub, and an AI
/// action on a cold LLM raises the sheet, starts the service with stages, then runs.
/// </summary>
[Collection(E2eCollection.Name)]
public class AiServicesTests(E2eAppFixture app, PlaywrightFixture pw) : WebE2eTestBase(app, pw)
{
    private ILocator Card(string name) => Page.Locator($".services-page__card[data-service='{name}']");

    [Fact]
    public async Task Shutdown_from_the_services_page_flips_the_chip_and_start_brings_it_back()
    {
        App.FakeControl.StatusByName["llama"] = AiServiceStatus.Ready;
        try
        {
            await GotoAppAsync("/app/settings/services");

            await Expect(Card("llama").Locator("r2m-docker-controls r2m-status-chip")).ToContainTextAsync("Ready");
            await Expect(Card("whisper")).ToContainTextAsync("read2me-whisper");

            await Card("llama").GetByRole(AriaRole.Button, new() { Name = "Shutdown" }).ClickAsync();
            await Expect(Card("llama").Locator("r2m-docker-controls r2m-status-chip"))
                .ToContainTextAsync("Stopped", new() { Timeout = 10_000 });
            await Expect(Page.Locator(".r2m-toast-panel", new() { HasText = "llama shut down" })).ToBeVisibleAsync();
            Assert.Contains("shutdown:llama", App.FakeControl.OpLog);

            await Card("llama").GetByRole(AriaRole.Button, new() { Name = "Start", Exact = true }).ClickAsync();
            await Expect(Card("llama").Locator("r2m-docker-controls r2m-status-chip"))
                .ToContainTextAsync("Ready", new() { Timeout = 10_000 });
        }
        finally
        {
            App.FakeControl.Reset();
        }
    }

    [Fact]
    public async Task Attribute_on_a_cold_llm_raises_the_sheet_and_the_job_runs_after_start_services()
    {
        await App.SeedThreeDialogParagraphProjectAsync("web-preflight", "Web Preflight Book", "A. Author", characterName: "Alice");
        App.FakeAi.LlmReply = p => FakeAiResponses.AttributionReply(p, "Alice");
        // The seeded LLM config's URL is what the fake resolves; only it is cold.
        App.FakeControl.StatusByName["http://fake-llm"] = AiServiceStatus.Stopped;
        try
        {
            await GotoAppAsync("/app/projects/web-preflight/book?mode=speakers");
            await Page.Locator("[data-node-id] .tree__select").First.CheckAsync();
            await Expect(Page.Locator("[data-testid='selection-count']")).ToHaveTextAsync("3 paragraphs");

            // Cancel first: nothing starts, nothing is queued.
            await Page.Locator("[data-action='attribute-selection']").ClickAsync();
            var sheet = Page.Locator("r2m-preflight-sheet");
            await Expect(sheet.Locator("h2")).ToHaveTextAsync("Attribution needs AI services");
            await Expect(sheet.Locator(".r2m-preflight-sheet__name")).ToHaveTextAsync("http://fake-llm");
            await sheet.GetByRole(AriaRole.Button, new() { Name = "Cancel" }).ClickAsync();
            await Expect(sheet).ToHaveCountAsync(0);
            Assert.Empty(App.FakeControl.OpLog);
            await Expect(Page.Locator("[data-testid='selection-count']")).ToHaveTextAsync("3 paragraphs");

            // Start services: the stage rows play out, the sheet closes and the job is queued.
            await Page.Locator("[data-action='attribute-selection']").ClickAsync();
            await sheet.GetByRole(AriaRole.Button, new() { Name = "Start services" }).ClickAsync();
            await Expect(sheet).ToHaveCountAsync(0, new() { Timeout = 10_000 });
            Assert.Contains("start:http://fake-llm", App.FakeControl.OpLog);

            await Expect(Page.Locator("r2m-item .r2m-speaker-chip--named", new() { HasText = "Alice" }))
                .ToHaveCountAsync(3, new() { Timeout = 20_000 });
        }
        finally
        {
            App.FakeControl.Reset();
            await App.WaitForQueueDrainAsync("/api/attribution/queue");
        }
    }
}
