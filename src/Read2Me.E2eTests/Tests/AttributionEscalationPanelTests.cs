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
    /// Drives the panel through the real Blazor circuit: the seeded "fake" config is the primary,
    /// a second "fake-big" config is added as an escalation step, and both the row and the
    /// self-consistency toggle must survive a full page reload (proving save-on-change persistence).
    /// Cleans up its own state afterwards because the app fixture is collection-shared.
    /// </summary>
    [Fact]
    public async Task Add_escalation_step_and_toggle_self_consistency_persist_across_reload()
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

            // Primary row shows the active "fake" config.
            await Expect(Page.Locator(".mud-chip", new() { HasText = "Primary (active)" })).ToBeVisibleAsync();

            // Add "fake-big" via the add-select.
            await Page.GetByLabel("Add escalation step").ClickAsync();
            await Page.GetByRole(AriaRole.Option, new() { Name = "fake-big" }).ClickAsync();

            // Save-on-change persists the chain — poll the DB until it lands.
            await PollAsync(async () =>
                (await settings.GetEscalationConfigIdsAsync()).SequenceEqual(new[] { big.Id }));

            // Toggle self-consistency on.
            await Page.GetByRole(AriaRole.Switch).ClickAsync();
            await PollAsync(async () => await settings.GetSelfConsistencyAsync());

            // Reload — persisted chain + toggle must come back.
            await GotoAsync("/llm-settings");

            Assert.Equal(new[] { big.Id }, await settings.GetEscalationConfigIdsAsync());
            Assert.True(await settings.GetSelfConsistencyAsync());

            // The escalation row (a body paragraph, not the config card heading) is present after reload.
            await Expect(Page.GetByRole(AriaRole.Paragraph)
                .Filter(new() { HasText = "fake-big" })).ToBeVisibleAsync();
        }
        finally
        {
            await settings.SetEscalationConfigIdsAsync(System.Array.Empty<int>());
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
