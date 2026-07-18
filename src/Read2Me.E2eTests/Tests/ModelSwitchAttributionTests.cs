using Microsoft.Playwright;
using Read2Me.E2eTests.Infrastructure;
using Read2Me.E2eTests.Infrastructure.FakeAi;

namespace Read2Me.E2eTests.Tests;

[Collection(E2eCollection.Name)]
public class ModelSwitchAttributionTests(E2eAppFixture app, PlaywrightFixture pw) : E2eTestBase(app, pw)
{
    /// <summary>
    /// The seeded llama config is switchable and targets <see cref="FakeAiRoutingHandler.DefaultModel"/>.
    /// Here that model starts <c>unloaded</c>, so the very first attribution request must drive the
    /// switch-and-wait gate: detect (unloaded) → autoload trigger → poll <c>GET /v1/models</c> until it
    /// reads <c>loaded</c> → send the real request. A resolved chip proves the whole path completed —
    /// the real request only runs once the model loaded.
    /// </summary>
    [Fact]
    public async Task Switch_and_wait_loads_the_model_then_attributes()
    {
        await App.SeedProjectAsync("switch-book", "Switch Book", "A. Author", characterName: "Alice");

        // Target model starts unloaded; an autoload request flips it loading→loaded after two polls.
        App.FakeAi.LlmModels = FakeLlmModelStore.Switching(
            target: FakeAiRoutingHandler.DefaultModel, loadsAfterPolls: 2);
        App.FakeAi.LlmReply = p => FakeAiResponses.AttributionReply(p, "Alice");

        await GotoAsync("/project/switch-book");

        await Page.GetByText("ch1").ClickAsync();

        var unknownChip = Page.Locator(".mud-chip", new() { HasText = "Unknown" });
        await Expect(unknownChip).ToBeVisibleAsync();

        await Page.Locator(".mud-checkbox input[type=checkbox]").First.CheckAsync(new() { Force = true });
        await Expect(Page.GetByText("1 paragraph selected")).ToBeVisibleAsync();

        await Page.GetByRole(AriaRole.Button, new() { Name = "Add to Character queue" }).ClickAsync();

        // Attribution only succeeds after switch-and-wait loads the model and the real request runs.
        await Expect(Page.Locator(".mud-chip", new() { HasText = "Alice" }))
            .ToBeVisibleAsync(new() { Timeout = 30_000 });
        await Expect(Page.Locator(".mud-chip", new() { HasText = "Unknown" }))
            .ToHaveCountAsync(0);
    }
}
