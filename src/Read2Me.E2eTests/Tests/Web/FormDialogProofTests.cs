using Microsoft.Playwright;
using Read2Me.E2eTests.Infrastructure;

namespace Read2Me.E2eTests.Tests.Web;

/// <summary>Throwaway proof for #484: feature dialogs are forms. Deleted in the PR's final commit.</summary>
public class FormDialogProofTests(E2eAppFixture app, PlaywrightFixture pw) : E2eTestBase(app, pw)
{
    [Fact]
    public async Task New_project_Create_on_an_empty_draft_focuses_Book_title_and_shows_every_error()
    {
        await GotoAppAsync("projects");
        await Page.GetByRole(AriaRole.Button, new() { Name = "New project" }).First.ClickAsync();
        var dialog = Page.GetByRole(AriaRole.Dialog, new() { Name = "New project" });
        await Expect(dialog).ToBeVisibleAsync();

        var create = dialog.Locator(".new-project__create");
        await Expect(create).ToBeEnabledAsync();
        await Expect(dialog.Locator(".r2m-field__error")).ToHaveCountAsync(0);

        await create.ClickAsync();

        await Expect(dialog.Locator("input[name='bookTitle']")).ToBeFocusedAsync();
        await Expect(dialog.Locator(".r2m-field__error")).ToHaveTextAsync(
            ["Book title is required", "Project title is required", "Author is required"]);
        await Expect(dialog.Locator(".new-project__file")).ToContainTextAsync("Choose a book file");
        await Expect(dialog).ToBeVisibleAsync();
    }

    [Fact]
    public async Task Theme_editor_hex_error_waits_for_blur_and_is_linked_to_its_input()
    {
        await GotoAppAsync("settings/themes");
        await Page.GetByRole(AriaRole.Button, new() { Name = "New theme" }).ClickAsync();
        var dialog = Page.GetByRole(AriaRole.Dialog, new() { Name = "New theme" });
        await Expect(dialog).ToBeVisibleAsync();

        var hex = dialog.GetByLabel("Primary hex");
        await hex.FillAsync("#1");
        var error = dialog.Locator("[data-colour='primary'] .editor__error");
        await Expect(error).ToHaveCountAsync(0);
        await Expect(hex).Not.ToHaveAttributeAsync("aria-invalid", "true");

        await Page.Keyboard.PressAsync("Tab");

        await Expect(error).ToHaveTextAsync("Use #rgb or #rrggbb");
        await Expect(hex).ToHaveAttributeAsync("aria-invalid", "true");
        var errorId = await error.GetAttributeAsync("id");
        Assert.False(string.IsNullOrEmpty(errorId));
        await Expect(hex).ToHaveAttributeAsync("aria-describedby", errorId!);
        Assert.Null(await error.GetAttributeAsync("role"));
    }

    [Fact]
    public async Task Enter_in_a_text_prompt_submits_it_and_a_composing_Enter_does_not()
    {
        var book = await App.SeedLongBookAsync("form-dialog-prompt", "Form Dialog Prompt", "A. Author", chapters: 2, paragraphs: 1);
        await GotoAppAsync("projects/form-dialog-prompt/book");
        await Expect(Page.Locator(".tree__title")).ToHaveTextAsync(["Chapter 1", "Chapter 2"]);

        var node = Page.Locator($"[data-node-id='{book.ChapterId("Chapter 2")}']");
        await node.HoverAsync();
        await node.Locator(".r2m-node-menu__trigger").ClickAsync();
        await Page.Locator("[role='menu'] [data-entry='edit-title']").ClickAsync();
        var dialog = Page.GetByRole(AriaRole.Dialog);
        await Expect(dialog).ToBeVisibleAsync();
        var input = dialog.Locator(".r2m-text-prompt-dialog__input");
        await Expect(input).ToBeFocusedAsync();
        await input.FillAsync("Renamed");

        await input.DispatchEventAsync("keydown", new { key = "Enter", isComposing = true, bubbles = true });
        await Expect(dialog).ToBeVisibleAsync();

        await Page.Keyboard.PressAsync("Enter");

        await Expect(dialog).ToBeHiddenAsync();
        await Expect(Page.Locator(".tree__title")).ToHaveTextAsync(["Chapter 1", "Renamed"]);
    }
}
