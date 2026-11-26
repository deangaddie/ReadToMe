using Microsoft.Playwright;
using Read2Me.E2eTests.Infrastructure;

namespace Read2Me.E2eTests.Tests.Web;

/// <summary>Throwaway proof for #468: a keyboard open enters the menu and Down moves focus.</summary>
public class MenuKeysProofTests(E2eAppFixture app, PlaywrightFixture pw) : E2eTestBase(app, pw)
{
    private const string FocusedAction = "() => document.activeElement?.dataset.action ?? document.activeElement?.className ?? null";

    [Fact]
    public async Task Project_card_actions_menu_opens_on_Enter_into_its_first_item_and_Down_moves()
    {
        await App.SeedProjectAsync("dracula", "Dracula", "Bram Stoker");
        await GotoAppAsync("projects");
        var trigger = Page.GetByRole(AriaRole.Button, new() { Name = "Actions for Dracula" });
        await trigger.FocusAsync();
        await Page.Keyboard.PressAsync("Enter");

        var menu = Page.GetByRole(AriaRole.Menu, new() { Name = "Actions for Dracula" });
        await Expect(menu).ToBeVisibleAsync();
        await Expect(menu.Locator(".r2m-project-card__open")).ToBeFocusedAsync();
        await Page.Keyboard.PressAsync("ArrowDown");
        await Expect(menu.Locator("[role=menuitem]").Nth(1)).ToBeFocusedAsync();
        Console.WriteLine($"project card: focus after Down = {await Page.EvaluateAsync<string>(FocusedAction)}");

        await Page.Keyboard.PressAsync("Escape");
        await Expect(menu).ToBeHiddenAsync();
        await Expect(trigger).ToBeFocusedAsync();
    }

    [Fact]
    public async Task Book_actions_menu_opens_on_Enter_into_its_first_item_and_Down_moves()
    {
        await App.SeedLongBookAsync("menu-keys", "Menu Keys", "A. Author", chapters: 2, paragraphs: 2);
        await GotoAppAsync("projects/menu-keys/book");
        var trigger = Page.GetByTestId("book-actions");
        await Expect(trigger).ToBeEnabledAsync();
        await trigger.FocusAsync();
        await Page.Keyboard.PressAsync("Enter");

        var menu = Page.GetByRole(AriaRole.Menu, new() { Name = "Book actions" });
        await Expect(menu).ToBeVisibleAsync();
        await Expect(menu.Locator("[data-action=reread]")).ToBeFocusedAsync();
        await Page.Keyboard.PressAsync("ArrowDown");
        await Expect(menu.Locator("[data-action=manual-reread]")).ToBeFocusedAsync();
        Console.WriteLine($"book actions: focus after Down = {await Page.EvaluateAsync<string>(FocusedAction)}");

        await Page.Keyboard.PressAsync("Escape");
        await Expect(menu).ToBeHiddenAsync();
        await Expect(trigger).ToBeFocusedAsync();
    }
}
