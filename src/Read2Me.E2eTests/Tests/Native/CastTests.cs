using System.Text.Json;
using Microsoft.Playwright;
using Read2Me.E2eTests.Infrastructure;

namespace Read2Me.E2eTests.Tests.Native;

/// <summary>
/// The native cast page's character detail (native-web 30, split from the Angular
/// <c>CastTests</c>): a rename and a new alias land in the host so every other reader sees the
/// same name. The discovery tests stay on Angular as <c>CastDiscoveryTests</c> until discovery
/// moves (native-web 31). Runs in Chromium and Firefox.
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
}
