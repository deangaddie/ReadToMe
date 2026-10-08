using Microsoft.Playwright;
using Read2Me.E2eTests.Infrastructure;
using Read2Me.E2eTests.Infrastructure.FakeAi;
using Read2Me.Services.Health;

namespace Read2Me.E2eTests.Tests.Native;

/// <summary>
/// The native preflight sheet (native-web 21), split out of the Angular <c>AiServicesTests</c>,
/// driven by the reader's Attribute action (native-web 25): on a cold LLM the action raises the
/// bottom sheet, Cancel starts nothing and keeps the selection, and Start services plays the
/// stages over the hub, closes the sheet and lets the action proceed — the paragraphs queue and
/// resolve from the fake AI. Runs in Chromium and Firefox.
/// </summary>
[Collection(E2eCollection.Name)]
public class PreflightTests(E2eAppFixture app, PlaywrightFixture pw) : E2eTestBase(app, pw)
{
    protected override WebApp WebApp => WebApp.Native;

    [Fact]
    public async Task Cold_llm_raises_the_sheet_cancel_starts_nothing_and_start_services_runs_the_plan()
    {
        await App.SeedThreeDialogParagraphProjectAsync("native-preflight", "Native Preflight Book", "A. Author", characterName: "Alice");
        App.FakeAi.LlmReply = p => FakeAiResponses.AttributionReply(p, "Alice");
        // The seeded LLM config's URL is what the fake resolves; only it is cold.
        App.FakeControl.StatusByName["http://fake-llm"] = AiServiceStatus.Stopped;
        try
        {
            await GotoAppAsync("projects/native-preflight/book?mode=speakers");
            await Page.Locator("[data-node-id] .tree__select").First.CheckAsync();
            var count = Page.Locator("[data-testid='selection-count']");
            await Expect(count).ToHaveTextAsync("3 paragraphs");
            var attribute = Page.Locator("[data-action='attribute-selection']");

            // Cancel first: nothing starts, nothing is queued, and the selection is kept.
            await attribute.ClickAsync();
            var dialog = Page.GetByRole(AriaRole.Dialog);
            var sheet = dialog.Locator("r2m-preflight-sheet");
            await Expect(dialog).ToHaveClassAsync(new System.Text.RegularExpressions.Regex("r2m-dialog--sheet"));
            await Expect(sheet.Locator("h2")).ToHaveTextAsync("Attribution needs AI services");
            await Expect(sheet.Locator(".r2m-preflight-sheet__name")).ToHaveTextAsync("http://fake-llm");
            await sheet.GetByRole(AriaRole.Button, new() { Name = "Cancel" }).ClickAsync();
            await Expect(sheet).ToHaveCountAsync(0);
            await Expect(count).ToHaveTextAsync("3 paragraphs");
            await Expect(Page.Locator(".r2m-toast")).ToHaveCountAsync(0);
            Assert.Empty(App.FakeControl.OpLog);

            // Start services: the stage rows play out, the sheet closes and the action runs.
            await attribute.ClickAsync();
            await Expect(sheet.Locator("h2")).ToHaveTextAsync("Attribution needs AI services");
            await sheet.GetByRole(AriaRole.Button, new() { Name = "Start services" }).ClickAsync();
            await Expect(sheet).ToHaveCountAsync(0, new() { Timeout = 10_000 });
            Assert.Contains("start:http://fake-llm", App.FakeControl.OpLog);
            await Expect(Page.Locator(".r2m-toast", new() { HasText = "Queued 3 paragraphs" })).ToHaveCountAsync(1);
            await Expect(Page.Locator("[data-testid='selection-bar']")).ToHaveCountAsync(0);
            await Expect(Page.Locator(".r2m-item .r2m-speaker-chip--named", new() { HasText = "Alice" }))
                .ToHaveCountAsync(3, new() { Timeout = 20_000 });
            await App.WaitForQueueDrainAsync("/api/attribution/queue");

            // Warm again: the gate answers at once, with no sheet, and the action runs straight away.
            await Page.Locator("[data-node-id] .tree__select").First.CheckAsync();
            await Expect(count).ToHaveTextAsync("3 paragraphs");
            await attribute.ClickAsync();
            await Expect(Page.Locator(".r2m-toast", new() { HasText = "Queued 3 paragraphs" })).ToHaveCountAsync(1);
            await Expect(Page.GetByRole(AriaRole.Dialog)).ToHaveCountAsync(0);
            await Expect(Page.Locator("[data-testid='selection-bar']")).ToHaveCountAsync(0);
        }
        finally
        {
            App.FakeControl.Reset();
            await App.WaitForQueueDrainAsync("/api/attribution/queue");
        }
    }
}
