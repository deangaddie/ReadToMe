using Microsoft.Playwright;
using Read2Me.E2eTests.Infrastructure;
using Read2Me.Services.Health;

namespace Read2Me.E2eTests.Tests.Native;

/// <summary>
/// The native preflight sheet (native-web 21), split out of the Angular <c>AiServicesTests</c>:
/// an AI action on a cold LLM raises the bottom sheet, Cancel starts nothing, and Start services
/// plays the stages over the hub, closes the sheet and lets the action proceed. No ported screen
/// carries an AI action yet (that is native-web 25), so the gate is driven the way those screens
/// will call it, through the app's <c>window.r2m.preflight.ensureReady</c> handle; the result it
/// resolves with is what the action would act on. Runs in Chromium and Firefox.
/// </summary>
[Collection(E2eCollection.Name)]
public class PreflightTests(E2eAppFixture app, PlaywrightFixture pw) : E2eTestBase(app, pw)
{
    private const string EnsureReady = "() => window.r2m.preflight.ensureReady('attribution')";

    protected override WebApp WebApp => WebApp.Native;

    [Fact]
    public async Task Cold_llm_raises_the_sheet_cancel_starts_nothing_and_start_services_runs_the_plan()
    {
        // The seeded LLM config's URL is what the fake resolves; only it is cold.
        App.FakeControl.StatusByName["http://fake-llm"] = AiServiceStatus.Stopped;
        try
        {
            await GotoAppAsync("projects");

            // Cancel first: nothing starts, and the action is told not to run.
            var cancelled = Page.EvaluateAsync<bool>(EnsureReady);
            var dialog = Page.GetByRole(AriaRole.Dialog);
            var sheet = dialog.Locator("r2m-preflight-sheet");
            await Expect(dialog).ToHaveClassAsync(new System.Text.RegularExpressions.Regex("r2m-dialog--sheet"));
            await Expect(sheet.Locator("h2")).ToHaveTextAsync("Attribution needs AI services");
            await Expect(sheet.Locator(".r2m-preflight-sheet__name")).ToHaveTextAsync("http://fake-llm");
            await sheet.GetByRole(AriaRole.Button, new() { Name = "Cancel" }).ClickAsync();
            await Expect(sheet).ToHaveCountAsync(0);
            Assert.False(await cancelled);
            Assert.Empty(App.FakeControl.OpLog);

            // Start services: the stage rows play out, the sheet closes and the action may run.
            var ready = Page.EvaluateAsync<bool>(EnsureReady);
            await Expect(sheet.Locator("h2")).ToHaveTextAsync("Attribution needs AI services");
            await sheet.GetByRole(AriaRole.Button, new() { Name = "Start services" }).ClickAsync();
            await Expect(sheet).ToHaveCountAsync(0, new() { Timeout = 10_000 });
            Assert.True(await ready);
            Assert.Contains("start:http://fake-llm", App.FakeControl.OpLog);

            // Warm again: the gate answers at once, with no sheet.
            Assert.True(await Page.EvaluateAsync<bool>(EnsureReady));
            await Expect(Page.GetByRole(AriaRole.Dialog)).ToHaveCountAsync(0);
        }
        finally
        {
            App.FakeControl.Reset();
        }
    }
}
