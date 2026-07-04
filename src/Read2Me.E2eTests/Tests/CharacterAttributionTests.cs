using Microsoft.Playwright;
using Read2Me.E2eTests.Infrastructure;
using Read2Me.E2eTests.Infrastructure.FakeAi;

namespace Read2Me.E2eTests.Tests;

[Collection(E2eCollection.Name)]
public class CharacterAttributionTests(E2eAppFixture app, PlaywrightFixture pw) : E2eTestBase(app, pw)
{
    [Fact]
    public async Task Selecting_paragraph_and_queueing_attributes_it_via_llm()
    {
        await App.SeedProjectAsync("attr-book", "Attribution Book", "A. Author", characterName: "Alice");
        App.FakeAi.LlmReply = _ => FakeAiResponses.AttributionReply("Alice");

        await GotoAsync("/project/attr-book");

        // Chapters are collapsed by default; expand ch1 to lazy-load its paragraphs.
        await Page.GetByText("ch1").ClickAsync();

        // The unattributed character paragraph shows an "Unknown" chip and a checkbox.
        var unknownChip = Page.Locator(".mud-chip", new() { HasText = "Unknown" });
        await Expect(unknownChip).ToBeVisibleAsync();

        await Page.Locator(".mud-checkbox input[type=checkbox]").First.CheckAsync(new() { Force = true });

        // Selection row of the status dock appears.
        await Expect(Page.GetByText("1 paragraph selected")).ToBeVisibleAsync();

        await Page.GetByRole(AriaRole.Button, new() { Name = "Add to Character queue" }).ClickAsync();

        // Fake LLM resolves the line to Alice; chip updates via SignalR push.
        await Expect(Page.Locator(".mud-chip", new() { HasText = "Alice" }))
            .ToBeVisibleAsync(new() { Timeout = 20_000 });
        await Expect(Page.Locator(".mud-chip", new() { HasText = "Unknown" }))
            .ToHaveCountAsync(0);
    }
}
