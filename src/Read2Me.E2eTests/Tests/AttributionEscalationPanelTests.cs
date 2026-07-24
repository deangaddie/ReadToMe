using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using Read2Me.AppData.Entities;
using Read2Me.E2eTests.Infrastructure;
using Read2Me.Services;

namespace Read2Me.E2eTests.Tests;

[Collection(E2eCollection.Name)]
public class AttributionEscalationPanelTests(E2eAppFixture app, PlaywrightFixture pw) : E2eTestBase(app, pw)
{
    /// <summary>
    /// Drives the flat chain panel through the real Blazor circuit: with the chain empty the fallback
    /// hint names the seeded "fake" default config; a second "fake-big" config is then added as a chain
    /// step, and both the row and the self-consistency toggle must survive a full page reload (proving
    /// save-on-change persistence). Cleans up its own state afterwards because the app fixture is
    /// collection-shared.
    /// </summary>
    [Fact]
    public async Task Add_chain_step_and_toggle_self_consistency_persist_across_reload()
    {
        using var scope = App.Services.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<LlmSettingsService>();
        var big = await settings.CreateConfigAsync(new LlmServerConfig
        {
            Name = "fake-big",
            BaseUrl = "http://fake-llm-big",
        });

        try
        {
            await GotoAsync("/llm-settings");

            // Empty chain surfaces the default "fake" config as the named fallback, not a primary row.
            await Expect(Page.GetByText("attribution falls back to the default config")).ToBeVisibleAsync();

            // Add "fake-big" via the add-select — it becomes a flat chain step.
            await Page.GetByLabel("Add chain step").ClickAsync();
            // Exact — each config is offered as four variants ("fake-big", "fake-big (simple)",
            // "fake-big (thinking)", "fake-big (simple, thinking)"); the bare name is the plain rung.
            await Page.GetByRole(AriaRole.Option, new() { Name = "fake-big", Exact = true }).ClickAsync();

            // Save-on-change persists the flat chain — poll the DB until it lands.
            await PollAsync(async () =>
                (await settings.GetAttributionChainEntriesAsync()).Any(e => e.ConfigId == big.Id));

            // Toggle self-consistency on.
            await Page.GetByRole(AriaRole.Switch).ClickAsync();
            await PollAsync(async () => await settings.GetSelfConsistencyAsync());

            // Reload — persisted chain + toggle must come back.
            await GotoAsync("/llm-settings");

            Assert.Contains(big.Id, (await settings.GetAttributionChainEntriesAsync()).Select(e => e.ConfigId));
            Assert.True(await settings.GetSelfConsistencyAsync());

            // The chain row (a body paragraph, not the config card heading) is present after reload.
            await Expect(Page.GetByRole(AriaRole.Paragraph)
                .Filter(new() { HasText = "fake-big" })).ToBeVisibleAsync();
        }
        finally
        {
            await settings.SetAttributionChainEntriesAsync(System.Array.Empty<AttributionChainEntry>());
            await settings.SetSelfConsistencyAsync(false);
            await settings.DeleteConfigAsync(big.Id);
        }
    }

    private static async Task PollAsync(Func<Task<bool>> condition, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (await condition()) return;
            await Task.Delay(100);
        }
        Assert.Fail("Condition not met within timeout.");
    }
}
