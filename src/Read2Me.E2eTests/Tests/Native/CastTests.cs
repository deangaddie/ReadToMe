using System.Text.Json;
using Microsoft.Playwright;
using Read2Me.E2eTests.Infrastructure;

namespace Read2Me.E2eTests.Tests.Native;

/// <summary>
/// The native cast page (native-web 30 and 31, the Angular <c>CastTests</c> made whole again):
/// a rename and a new alias land in the host so every other reader sees the same name, and
/// discovery on the fake-AI host — review, edit a row, apply, the roster updates, a re-run marks
/// the row "Already exists", and a failure stays open with its reason. Runs in Chromium and Firefox.
/// </summary>
[Collection(E2eCollection.Name)]
public class CastTests(E2eAppFixture app, PlaywrightFixture pw) : E2eTestBase(app, pw)
{
    private static readonly HttpClient Http = new();

    protected override WebApp WebApp => WebApp.Native;

    [Fact]
    public async Task Rename_and_alias_land_in_the_host()
    {
        var book = await App.SeedThreeDialogParagraphProjectAsync("web-cast-edit", "Web Cast Edit", "A. Author", characterName: "Alice");
        var alice = book.CharacterId("Alice");

        await GotoAppAsync($"projects/web-cast-edit/cast/{alice}");
        await Expect(Page.Locator("r2m-cast-page")).ToBeVisibleAsync();
        var detail = Page.Locator("r2m-character-detail");
        await Expect(detail).ToBeVisibleAsync();

        await detail.Locator(".r2m-inline-edit__display").ClickAsync();
        await detail.Locator(".r2m-inline-edit__input").FillAsync("Alice Liddell");
        await detail.Locator(".r2m-inline-edit__input").PressAsync("Enter");
        await Expect(Page.Locator($".cast__row[data-character-id='{alice}']")).ToContainTextAsync("Alice Liddell");

        await detail.Locator("[data-action='add-alias']").ClickAsync();
        await detail.Locator("input[aria-label='New alias']").FillAsync("Al");
        await detail.Locator("input[aria-label='New alias']").PressAsync("Enter");
        await Expect(detail.Locator(".character-detail__alias[data-alias='Al']")).ToBeVisibleAsync();

        var characters = JsonDocument.Parse(await Http.GetStringAsync($"{App.BaseUrl}/api/projects/web-cast-edit/characters")).RootElement;
        var renamed = characters.EnumerateArray().Single(c => c.GetProperty("id").GetGuid() == alice);
        Assert.Equal("Alice Liddell", renamed.GetProperty("name").GetString());
        Assert.Contains("Al", renamed.GetProperty("aliases").EnumerateArray().Select(a => a.GetProperty("name").GetString()));
    }

    [Fact]
    public async Task Discover_edit_apply_updates_the_roster_and_a_rerun_marks_rows_existing()
    {
        await App.SeedThreeDialogParagraphProjectAsync("web-cast-discover", "Web Cast Book", "A. Author", characterName: "Alice");
        App.FakeAi.LlmReply = _ =>
            """{ "reasoning": "outline", "characters": [ { "name": "Alice", "aliases": ["Al"] }, { "name": "Bob", "aliases": ["Robert"] } ] }""";

        await GotoAppAsync("projects/web-cast-discover/cast");
        await Expect(Page.Locator("r2m-cast-page")).ToBeVisibleAsync();
        await Expect(Page.Locator(".cast__row")).ToHaveCountAsync(2);

        await Page.Locator("[data-action='discover']").ClickAsync();
        var dialog = Page.Locator("r2m-discovery-dialog");
        await Expect(dialog.Locator("[data-phase]")).ToHaveAttributeAsync("data-phase", "review", new() { Timeout = 15_000 });
        await Expect(dialog.Locator(".discover__row")).ToHaveCountAsync(2);
        // Alice is on the roster already; Bob is new.
        await Expect(dialog.Locator(".discover__row[data-row='0'] .r2m-status-chip")).ToContainTextAsync("Already exists");
        await Expect(dialog.Locator(".discover__row[data-row='1'] .r2m-status-chip")).ToHaveCountAsync(0);

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
        await Expect(dialog.Locator("[data-phase]")).ToHaveAttributeAsync("data-phase", "review", new() { Timeout = 15_000 });
        await Expect(dialog.Locator(".r2m-status-chip", new() { HasText = "Already exists" })).ToHaveCountAsync(1);
        await Expect(dialog.Locator("[data-testid='collision-warning']")).ToContainTextAsync("Robert");
        // Dropping the row clears the warning.
        await dialog.Locator(".discover__row[data-row='1'] input[type='checkbox']").UncheckAsync();
        await Expect(dialog.Locator("[data-testid='collision-warning']")).ToHaveCountAsync(0);
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Close" }).ClickAsync();
    }

    [Fact]
    public async Task Discovery_failure_stays_open_with_the_reason_and_rerun()
    {
        await App.SeedProjectAsync("web-cast-fail", "Web Cast Fail", "A. Author");
        App.FakeAi.LlmReply = _ => "not json at all";

        await GotoAppAsync("projects/web-cast-fail/cast?discover=1");
        await Expect(Page.Locator("r2m-cast-page")).ToBeVisibleAsync();
        var dialog = Page.Locator("r2m-discovery-dialog");
        await Expect(dialog.Locator("[data-phase]")).ToHaveAttributeAsync("data-phase", "failed", new() { Timeout = 15_000 });
        await Expect(dialog.Locator(".discover__error")).ToContainTextAsync("Character discovery failed");
        await Expect(dialog.Locator("[data-action='rerun']")).ToBeEnabledAsync();
        // The query param was consumed so a reload does not reopen the dialog.
        Assert.DoesNotContain("discover=1", Page.Url);
    }
}
