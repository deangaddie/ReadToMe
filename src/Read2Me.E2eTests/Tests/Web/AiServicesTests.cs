using Microsoft.Playwright;
using Read2Me.E2eTests.Infrastructure;
using Read2Me.E2eTests.Infrastructure.FakeAi;
using Read2Me.Services.Health;

namespace Read2Me.E2eTests.Tests.Web;

/// <summary>
/// The services dashboard (Angular ticket 25) against the real host and the fake container
/// controller: a lifecycle op from the page flips the chip over the hub. The preflight sheet test
/// moved to the native app with the sheet (<c>Tests/Native/PreflightTests</c>, native-web 21); this
/// class moves with the Settings screens.
/// </summary>
[Collection(E2eCollection.Name)]
public class AiServicesTests(E2eAppFixture app, PlaywrightFixture pw) : E2eTestBase(app, pw)
{
    private ILocator Card(string name) => Page.Locator($".services-page__card[data-service='{name}']");

    [Fact]
    public async Task Shutdown_from_the_services_page_flips_the_chip_and_start_brings_it_back()
    {
        App.FakeControl.StatusByName["llama"] = AiServiceStatus.Ready;
        try
        {
            await GotoAppAsync("settings/services");

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

}
