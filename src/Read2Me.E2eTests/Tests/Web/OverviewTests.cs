using System.Text.RegularExpressions;
using Read2Me.E2eTests.Infrastructure;
using Read2Me.E2eTests.Infrastructure.FakeAi;

namespace Read2Me.E2eTests.Tests.Web;

/// <summary>
/// The project overview's pipeline stepper (Angular ticket 09): the Attribute step is the next
/// step for a book with unassigned speakers, its primary action queues the whole book through the
/// fake LLM, and the step turns done from hub roll-ups without a reload.
/// </summary>
[Collection(E2eCollection.Name)]
public class OverviewTests(E2eAppFixture app, PlaywrightFixture pw) : WebE2eTestBase(app, pw)
{
    [Fact]
    public async Task Attribute_from_the_stepper_advances_the_step_live()
    {
        await App.SeedThreeDialogParagraphProjectAsync("web-overview", "Web Overview Book", "A. Author", characterName: "Alice");
        App.FakeAi.LlmReply = p => FakeAiResponses.AttributionReply(p, "Alice");
        try
        {
            await GotoAppAsync("/app/projects/web-overview");

            var steps = Page.Locator("r2m-pipeline .r2m-pipeline__step");
            await Expect(steps).ToHaveCountAsync(6);
            await Expect(steps.Nth(0)).ToHaveClassAsync(new Regex("r2m-pipeline__step--done"));

            var attribute = Page.Locator("r2m-pipeline [data-step='attribute']");
            await Expect(attribute).ToHaveAttributeAsync("aria-current", "step");
            await Expect(attribute.Locator(".r2m-pipeline__chip")).ToContainTextAsync("3 remaining");

            await attribute.Locator("[data-action='attribute']").ClickAsync();

            await Expect(attribute.Locator(".r2m-pipeline__chip")).ToContainTextAsync("Done", new() { Timeout = 20_000 });
            await Expect(attribute).Not.ToHaveAttributeAsync("aria-current", "step");
            await Expect(attribute.Locator("[data-action='attribute']")).ToBeDisabledAsync();

            // Voices is now the next step; the host agrees nothing is left to attribute.
            await Expect(Page.Locator("r2m-pipeline [data-step='voices']")).ToHaveAttributeAsync("aria-current", "step");
            var status = await Page.APIRequest.GetAsync($"{App.BaseUrl}/api/projects/web-overview/status");
            Assert.Contains("\"remaining\":0", await status.TextAsync());
        }
        finally
        {
            await App.WaitForQueueDrainAsync("/api/attribution/queue");
        }
    }
}
