using Read2Me.E2eTests.Infrastructure;

namespace Read2Me.E2eTests.Tests.Web;

/// <summary>Throwaway proof for #463: a destructive confirm opens on Cancel, so Enter keeps the project.</summary>
public class DestructiveConfirmFocusProofTests(E2eAppFixture app, PlaywrightFixture pw) : E2eTestBase(app, pw)
{
    private static readonly HttpClient Http = new();

    [Fact]
    public async Task Enter_straight_after_a_delete_project_confirm_opens_keeps_the_project()
    {
        await App.SeedProjectAsync("dracula", "Dracula", "Bram Stoker");
        await GotoAppAsync("projects");
        var card = Page.Locator(".r2m-project-card", new() { HasText = "Dracula" });
        await card.Locator(".r2m-project-card__more").ClickAsync();
        await card.Locator(".r2m-project-card__delete").ClickAsync();

        var dialog = Page.Locator("dialog r2m-confirm-dialog");
        await Expect(dialog).ToBeVisibleAsync();
        await Expect(dialog.Locator(".r2m-confirm-dialog__cancel")).ToBeFocusedAsync();
        await Page.Keyboard.PressAsync("Enter");
        await Expect(Page.Locator("dialog")).ToHaveCountAsync(0);

        await Expect(card).ToBeVisibleAsync();
        var projects = await Http.GetStringAsync($"{App.BaseUrl}/api/projects");
        Assert.Contains("\"dracula\"", projects);
    }
}
