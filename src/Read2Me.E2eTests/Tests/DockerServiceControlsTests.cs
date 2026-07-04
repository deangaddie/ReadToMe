using Microsoft.Playwright;
using Read2Me.E2eTests.Infrastructure;
using Read2Me.Services.Health;

namespace Read2Me.E2eTests.Tests;

[Collection(E2eCollection.Name)]
public class DockerServiceControlsTests(E2eAppFixture app, PlaywrightFixture pw) : E2eTestBase(app, pw)
{
    [Fact]
    public async Task Status_chip_renders_and_shutdown_transitions_to_stopped()
    {
        App.FakeControl.Status = AiServiceStatus.Ready;

        await GotoAsync("/llm-settings");

        // The seeded LLM config card resolves to a managed container → Ready chip + Shutdown button.
        await Expect(Page.Locator(".mud-chip", new() { HasText = "Ready" })).ToBeVisibleAsync();

        await Page.GetByRole(AriaRole.Button, new() { Name = "Shutdown" }).ClickAsync();

        // Shutdown flips the facade to Stopped; the chip re-renders and a Start button appears.
        await Expect(Page.Locator(".mud-chip", new() { HasText = "Stopped" }))
            .ToBeVisibleAsync(new() { Timeout = 10_000 });
        await Expect(Page.GetByRole(AriaRole.Button, new() { Name = "Start" })).ToBeVisibleAsync();
    }
}
