using System.Text.Json;
using Microsoft.Playwright;
using Read2Me.E2eTests.Infrastructure;
using Read2Me.Services.Llm;

namespace Read2Me.E2eTests.Tests.Web;

/// <summary>
/// The prompts settings page (Angular ticket 23) against the real host: edit the character prompt,
/// preview it with the sample book, save it, see the override and its compatibility warning in
/// Blazor's page too, then reset to the built-in default.
/// </summary>
[Collection(E2eCollection.Name)]
public class PromptSettingsTests(E2eAppFixture app, PlaywrightFixture pw) : WebE2eTestBase(app, pw)
{
    private static readonly HttpClient Http = new();

    private ILocator Template => Page.Locator("app-prompt-editor [data-field='template']");
    private ILocator Save => Page.Locator("r2m-config-editor-frame button", new() { HasText = "Save" });
    private ILocator Dirty => Page.Locator(".r2m-config-editor-frame__dirty");
    private ILocator CharacterRow => Page.Locator(".prompts__row", new() { Has = Page.Locator("[data-kind='character']") });

    private async Task<JsonElement> CatalogEntryAsync(string kind) =>
        JsonDocument.Parse(await Http.GetStringAsync($"{App.BaseUrl}/api/settings/prompts/catalog"))
            .RootElement.EnumerateArray().Single(k => k.GetProperty("kind").GetString() == kind);

    [Fact]
    public async Task Character_prompt_edit_preview_save_shows_in_Blazor_and_reset_restores_default()
    {
        const string edited = "Who speaks in {{book_title}}?\n{{context_json}}\n{{response_format}}";
        try
        {
            await GotoAppAsync("/app/settings/prompts");
            await Expect(Page.Locator(".r2m-config-editor-frame__title")).ToHaveTextAsync("Book Character Prompt");
            await Expect(Template).ToHaveValueAsync(PromptTemplates.DefaultCharacterPrompt);
            await Expect(Save).ToBeDisabledAsync();
            await Expect(CharacterRow.Locator("[data-role='warning-icon']")).ToHaveCountAsync(0);

            // Token chips insert at the caret.
            await Template.FillAsync("Who speaks in ?");
            await Template.EvaluateAsync("t => t.setSelectionRange(14, 14)");
            await Page.Locator("[data-token='book_title']").ClickAsync();
            await Expect(Template).ToHaveValueAsync("Who speaks in {{book_title}}?");

            // The preview renders the unsaved draft with the sample book.
            await Template.FillAsync(edited);
            await Page.Locator("app-prompt-editor mat-expansion-panel-header").ClickAsync();
            var preview = Page.Locator("[data-role='preview']");
            await Expect(preview).ToContainTextAsync("Who speaks in The Hobbit?");
            await Expect(preview).ToContainTextAsync("going on an adventure!"); // the sample context, JSON-escaped
            await Expect(Dirty).ToHaveCountAsync(1);

            await Save.ClickAsync();
            await Expect(Dirty).ToHaveCountAsync(0);
            await Expect(Page.Locator("[data-role='prompt-warning']"))
                .ToContainTextAsync("Stored override missing {{narrator_identity}}");
            await Expect(CharacterRow.Locator("r2m-status-chip")).ToContainTextAsync("Overridden");
            await Expect(CharacterRow.Locator("[data-role='warning-icon']")).ToHaveCountAsync(1);

            var stored = await CatalogEntryAsync("character");
            Assert.True(stored.GetProperty("isOverridden").GetBoolean());
            Assert.Equal(edited, stored.GetProperty("template").GetString());

            // Blazor's page shows the same override and the same warning.
            await GotoAsync("/llm-prompts");
            await Expect(Page.Locator("textarea").First).ToHaveValueAsync(edited);
            await Expect(Page.Locator(".mud-chip", new() { HasText = "Stored override missing" })).ToBeVisibleAsync();

            // Reset to default from the Angular page.
            await GotoAppAsync("/app/settings/prompts");
            await Expect(Template).ToHaveValueAsync(edited);
            await Page.Locator("[data-action='reset-prompt']").ClickAsync();
            await Page.Locator("r2m-confirm-dialog button", new() { HasText = "Reset" }).ClickAsync();
            await Expect(Template).ToHaveValueAsync(PromptTemplates.DefaultCharacterPrompt);
            await Expect(Page.Locator("[data-role='prompt-warning']")).ToHaveCountAsync(0);
            await Expect(CharacterRow.Locator("r2m-status-chip")).ToHaveCountAsync(0);

            stored = await CatalogEntryAsync("character");
            Assert.False(stored.GetProperty("isOverridden").GetBoolean());
        }
        finally
        {
            await Http.DeleteAsync($"{App.BaseUrl}/api/settings/prompts/character");
        }
    }
}
