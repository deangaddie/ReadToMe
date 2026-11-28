using Microsoft.Playwright;
using Read2Me.E2eTests.Infrastructure;

namespace Read2Me.E2eTests.Tests.Web;

/// <summary>Throwaway proof for #487: Enter and Escape mid IME composition do nothing in the speaker search, voice name and alias inputs. Deleted in the PR's final commit.</summary>
public class ImeCompositionProofTests(E2eAppFixture app, PlaywrightFixture pw) : E2eTestBase(app, pw)
{
    // Playwright can't drive a real IME: a synthetic keydown carrying isComposing stands in.
    private static Task ComposingAsync(ILocator input, string key) => input.EvaluateAsync(
        "(el, key) => el.dispatchEvent(new KeyboardEvent('keydown', { key, isComposing: true, bubbles: true, cancelable: true }))",
        key);

    [Fact]
    public async Task Speaker_search_neither_picks_nor_creates_on_a_composing_Enter()
    {
        var folder = $"ime-menu-{Guid.NewGuid():N}";
        var book = await App.SeedProjectAsync(folder, "Ime Menu", "Author");
        await GotoAppAsync($"projects/{folder}/book?mode=speakers");
        var item = Page.Locator($"[data-item-id='{book.ItemId("line1")}']");
        var chip = item.Locator(".r2m-speaker-chip__body");
        var menu = item.Locator(".r2m-speaker-chip__menu");
        var search = menu.GetByLabel("Search characters", new() { Exact = true });

        // A match is highlighted: a composing Enter does not pick it.
        await chip.ClickAsync();
        await Expect(menu).ToBeVisibleAsync();
        await search.FillAsync("Ali");
        await ComposingAsync(search, "Enter");
        await Page.WaitForTimeoutAsync(500);
        await Expect(menu).ToBeVisibleAsync();
        await Expect(item.Locator(".r2m-speaker-chip__name")).Not.ToHaveTextAsync("Alice");

        // No match: a composing Enter does not create a character named after the query.
        await search.FillAsync("にほん");
        await ComposingAsync(search, "Enter");
        await Page.WaitForTimeoutAsync(500);
        await Expect(menu).ToBeVisibleAsync();
        await Expect(item.Locator(".r2m-speaker-chip__name")).Not.ToHaveTextAsync("にほん");

        // A plain Enter commits: the character is created and assigned.
        await search.FillAsync("日本");
        await search.PressAsync("Enter");
        await Expect(item.Locator(".r2m-speaker-chip__name")).ToHaveTextAsync("日本");
    }

    [Fact]
    public async Task Alias_and_voice_name_ignore_a_composing_Enter_and_Escape()
    {
        var folder = $"ime-cast-{Guid.NewGuid():N}";
        var book = await App.SeedProjectAsync(folder, "Ime Cast", "Author");
        await GotoAppAsync($"projects/{folder}/cast/{book.CharacterId("Alice")}");

        // Character alias.
        await Page.Locator("[data-action='add-alias']").ClickAsync();
        var alias = Page.GetByRole(AriaRole.Textbox, new() { Name = "New alias", Exact = true });
        await alias.FillAsync("にほん");
        await ComposingAsync(alias, "Enter");
        await ComposingAsync(alias, "Escape");
        await Page.WaitForTimeoutAsync(500);
        await Expect(alias).ToBeVisibleAsync();
        await Expect(alias).ToHaveValueAsync("にほん");
        await Expect(Page.GetByRole(AriaRole.Button, new() { Name = "Remove alias にほん" })).ToHaveCountAsync(0);
        await alias.FillAsync("日本");
        await alias.PressAsync("Enter");
        await Expect(Page.GetByRole(AriaRole.Button, new() { Name = "Remove alias 日本" })).ToBeVisibleAsync();

        // Add voice name.
        await Page.Locator("[data-action='add-voice']").ClickAsync();
        var dialog = Page.Locator("r2m-add-voice-dialog");
        var name = dialog.GetByRole(AriaRole.Textbox, new() { Name = "Voice name", Exact = true });
        await Expect(name).ToBeVisibleAsync();
        await name.FillAsync("にほん");
        await ComposingAsync(name, "Enter");
        await Page.WaitForTimeoutAsync(500);
        await Expect(dialog).ToBeVisibleAsync();
        await name.FillAsync("日本 Voice");
        await name.PressAsync("Enter");
        await Expect(dialog).ToHaveCountAsync(0);
        await Expect(Page.GetByText("日本 Voice").First).ToBeVisibleAsync();
    }

    [Fact]
    public async Task Discovery_alias_ignores_a_composing_Enter_and_Escape()
    {
        var folder = $"ime-disc-{Guid.NewGuid():N}";
        await App.SeedProjectAsync(folder, "Ime Disc", "Author");
        App.FakeAi.LlmReply = _ =>
            """{ "reasoning": "outline scan", "characters": [ { "name": "Mina", "aliases": ["Madam Mina"] } ] }""";
        await GotoAppAsync($"projects/{folder}/cast");
        await Page.Locator("[data-action='discover']").ClickAsync();
        var dialog = Page.Locator("r2m-discovery-dialog");
        await dialog.Locator(".discover__add-alias").First.ClickAsync();
        var alias = dialog.GetByRole(AriaRole.Textbox, new() { Name = "New alias", Exact = true });
        await alias.FillAsync("にほん");
        await ComposingAsync(alias, "Enter");
        await ComposingAsync(alias, "Escape");
        await Page.WaitForTimeoutAsync(500);
        await Expect(dialog).ToBeVisibleAsync();
        await Expect(alias).ToBeVisibleAsync();
        await Expect(alias).ToHaveValueAsync("にほん");
        await Expect(dialog.GetByRole(AriaRole.Button, new() { Name = "Remove alias にほん" })).ToHaveCountAsync(0);
        await alias.FillAsync("日本");
        await alias.PressAsync("Enter");
        await Expect(dialog.GetByRole(AriaRole.Button, new() { Name = "Remove alias 日本" })).ToBeVisibleAsync();
    }
}
