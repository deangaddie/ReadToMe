using System.Text.Json;
using Microsoft.Playwright;
using Read2Me.E2eTests.Infrastructure;

namespace Read2Me.E2eTests.Tests.Web;

/// <summary>
/// The Angular cast page's discovery (ticket 15) on the fake-AI host: review, edit a row, apply,
/// the roster updates, a re-run marks the row "Already exists", and a failure stays open with its
/// reason. Split from <c>CastTests</c> by native-web 30, which moved the rename and alias test to
/// <c>Tests/Native</c>; these move with discovery (native-web 31).
/// </summary>
[Collection(E2eCollection.Name)]
public class CastDiscoveryTests(E2eAppFixture app, PlaywrightFixture pw) : E2eTestBase(app, pw)
{
    private static readonly HttpClient Http = new();

    [Fact]
    public async Task Discover_edit_apply_updates_the_roster_and_a_rerun_marks_rows_existing()
    {
        await App.SeedThreeDialogParagraphProjectAsync("web-cast-discover", "Web Cast Book", "A. Author", characterName: "Alice");
        App.FakeAi.LlmReply = _ =>
            """{ "reasoning": "outline", "characters": [ { "name": "Alice", "aliases": ["Al"] }, { "name": "Bob", "aliases": ["Robert"] } ] }""";

        await GotoAppAsync("projects/web-cast-discover/cast");
        await Expect(Page.Locator(".cast__row")).ToHaveCountAsync(2);

        await Page.Locator("[data-action='discover']").ClickAsync();
        var dialog = Page.Locator("app-discovery-dialog");
        await Expect(dialog.Locator("mat-dialog-content")).ToHaveAttributeAsync("data-phase", "review", new() { Timeout = 15_000 });
        await Expect(dialog.Locator(".discover__row")).ToHaveCountAsync(2);
        // Alice is on the roster already; Bob is new.
        await Expect(dialog.Locator(".discover__row[data-row='0'] r2m-status-chip")).ToContainTextAsync("Already exists");
        await Expect(dialog.Locator(".discover__row[data-row='1'] r2m-status-chip")).ToHaveCountAsync(0);

        // Edit Bob's row: rename and add an alias, then apply both rows.
        var bob = dialog.Locator(".discover__row[data-row='1']");
        await bob.Locator("input[aria-label='Character name']").FillAsync("Robert Bobbington");
        await bob.Locator(".discover__add-alias").ClickAsync();
        await bob.Locator("input[aria-label='New alias']").FillAsync("Bobby");
        await bob.Locator("input[aria-label='New alias']").PressAsync("Enter");
        await Expect(bob.Locator(".discover__alias")).ToHaveCountAsync(2);
        await dialog.Locator("[data-action='apply']").ClickAsync();
        await Expect(dialog).ToHaveCountAsync(0);

        await Expect(Page.Locator(".cast__row")).ToHaveCountAsync(3);
        await Expect(Page.Locator(".cast__row").Nth(2)).ToContainTextAsync("Robert Bobbington");
        var roster = JsonDocument.Parse(await Http.GetStringAsync($"{App.BaseUrl}/api/projects/web-cast-discover/characters/summary")).RootElement;
        var robert = roster.EnumerateArray().Single(c => c.GetProperty("name").GetString() == "Robert Bobbington");
        Assert.Equal(new HashSet<string> { "Robert", "Bobby" },
            robert.GetProperty("aliases").EnumerateArray().Select(a => a.GetProperty("name").GetString()!).ToHashSet());

        // Run again: Alice resolves onto the roster by name. "Bob" does not — he was renamed — and
        // his alias "Robert" now belongs to Robert Bobbington, so the review warns about it.
        await Page.Locator("[data-action='discover']").ClickAsync();
        await Expect(dialog.Locator("mat-dialog-content")).ToHaveAttributeAsync("data-phase", "review", new() { Timeout = 15_000 });
        await Expect(dialog.Locator("r2m-status-chip", new() { HasText = "Already exists" })).ToHaveCountAsync(1);
        await Expect(dialog.Locator("[data-testid='collision-warning']")).ToContainTextAsync("Robert");
        // Dropping the row clears the warning.
        await dialog.Locator(".discover__row[data-row='1'] mat-checkbox input").UncheckAsync();
        await Expect(dialog.Locator("[data-testid='collision-warning']")).ToHaveCountAsync(0);
        await dialog.Locator("mat-dialog-actions button", new() { HasText = "Close" }).ClickAsync();
    }

    [Fact]
    public async Task Discovery_failure_stays_open_with_the_reason_and_rerun()
    {
        await App.SeedProjectAsync("web-cast-fail", "Web Cast Fail", "A. Author");
        App.FakeAi.LlmReply = _ => "not json at all";

        await GotoAppAsync("projects/web-cast-fail/cast?discover=1");
        var dialog = Page.Locator("app-discovery-dialog");
        await Expect(dialog.Locator("mat-dialog-content")).ToHaveAttributeAsync("data-phase", "failed", new() { Timeout = 15_000 });
        await Expect(dialog.Locator(".discover__error")).ToContainTextAsync("Character discovery failed");
        await Expect(dialog.Locator("[data-action='rerun']")).ToBeEnabledAsync();
        // The query param was consumed so a reload does not reopen the dialog.
        Assert.DoesNotContain("discover=1", Page.Url);
    }
}
