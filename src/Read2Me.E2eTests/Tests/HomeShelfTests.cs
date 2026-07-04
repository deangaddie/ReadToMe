using Read2Me.E2eTests.Infrastructure;

namespace Read2Me.E2eTests.Tests;

[Collection(E2eCollection.Name)]
public class HomeShelfTests(E2eAppFixture app, PlaywrightFixture pw) : E2eTestBase(app, pw)
{
    [Fact]
    public async Task Shelf_shows_seeded_project_card_and_settings_nav()
    {
        await App.SeedProjectAsync("shelf-book", "The Shelf Book", "Shelby Author");

        await GotoAsync("/");

        var card = Page.Locator(".project-card", new() { HasText = "The Shelf Book" });
        await Expect(card).ToBeVisibleAsync();
        await Expect(card.GetByText("Shelby Author")).ToBeVisibleAsync();

        var settingsGroup = Page.Locator(".mud-nav-group", new() { HasText = "Settings" });
        await Expect(settingsGroup).ToBeVisibleAsync();
    }
}
