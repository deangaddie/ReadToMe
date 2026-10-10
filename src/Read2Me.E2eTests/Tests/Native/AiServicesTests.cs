using Microsoft.Playwright;
using Read2Me.E2eTests.Infrastructure;
using Read2Me.E2eTests.Infrastructure.FakeAi;
using Read2Me.Services.Health;

namespace Read2Me.E2eTests.Tests.Native;

/// <summary>
/// The native services dashboard (native-web 36, moved from the Angular ticket 25 suite) against
/// the real host and the fake container controller: a lifecycle op from the page flips the chip
/// over the hub. The preflight sheet test lives in <c>PreflightTests</c>. Runs in Chromium and
/// Firefox.
/// </summary>
[Collection(E2eCollection.Name)]
public class AiServicesTests(E2eAppFixture app, PlaywrightFixture pw) : E2eTestBase(app, pw)
{
    protected override WebApp WebApp => WebApp.Native;

    private ILocator Card(string name) => Page.Locator($"r2m-ai-services-page .services-page__card[data-service='{name}']");
    private static ILocator Chip(ILocator card) => card.Locator(".r2m-docker-controls .r2m-status-chip");

    [Fact]
    public async Task Shutdown_from_the_services_page_flips_the_chip_and_start_brings_it_back()
    {
        App.FakeControl.StatusByName["llama"] = AiServiceStatus.Ready;
        try
        {
            await GotoAppAsync("settings/services");

            await Expect(Chip(Card("llama"))).ToContainTextAsync("Ready");
            await Expect(Card("whisper")).ToContainTextAsync("read2me-whisper");

            await Card("llama").GetByRole(AriaRole.Button, new() { Name = "Shutdown" }).ClickAsync();
            await Expect(Chip(Card("llama"))).ToContainTextAsync("Stopped", new() { Timeout = 10_000 });
            await Expect(Page.Locator(".r2m-toast", new() { HasText = "llama shut down" })).ToBeVisibleAsync();
            Assert.Contains("shutdown:llama", App.FakeControl.OpLog);

            await Card("llama").GetByRole(AriaRole.Button, new() { Name = "Start", Exact = true }).ClickAsync();
            await Expect(Chip(Card("llama"))).ToContainTextAsync("Ready", new() { Timeout = 10_000 });
        }
        finally
        {
            App.FakeControl.Reset();
        }
    }
}
