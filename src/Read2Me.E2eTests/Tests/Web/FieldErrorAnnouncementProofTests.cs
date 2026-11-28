using Microsoft.Playwright;
using Read2Me.E2eTests.Infrastructure;

namespace Read2Me.E2eTests.Tests.Web;

/// <summary>Throwaway proof for #485: field errors are announced once on blur. Deleted in the PR's final commit.</summary>
public class FieldErrorAnnouncementProofTests(E2eAppFixture app, PlaywrightFixture pw) : E2eTestBase(app, pw)
{
    [Fact]
    public async Task Blurring_an_empty_Book_title_announces_its_error_once_and_describes_the_input()
    {
        // A spy on ariaNotify, which the shared announcer calls on the open modal or the document.
        await Page.AddInitScriptAsync("""
            window.__said = [];
            const spy = function (text, options) { window.__said.push({ text, priority: options?.priority }); };
            Element.prototype.ariaNotify = spy;
            Document.prototype.ariaNotify = spy;
            """);
        await GotoAppAsync("projects");
        await Page.GetByRole(AriaRole.Button, new() { Name = "New project" }).First.ClickAsync();
        var dialog = Page.GetByRole(AriaRole.Dialog, new() { Name = "New project" });
        await Expect(dialog).ToBeVisibleAsync();

        var bookTitle = dialog.GetByRole(AriaRole.Textbox, new() { Name = "Book title" });
        await Expect(bookTitle).ToBeFocusedAsync();
        await Expect(bookTitle).ToHaveAttributeAsync("aria-invalid", "false");

        await Page.Keyboard.PressAsync("Tab");

        var error = dialog.Locator(".r2m-field__error");
        await Expect(error).ToHaveTextAsync("Book title is required");
        Assert.Null(await error.GetAttributeAsync("role"));
        await Expect(bookTitle).ToHaveAttributeAsync("aria-invalid", "true");
        await Expect(bookTitle).ToHaveAccessibleDescriptionAsync("Book title is required");

        await Page.WaitForFunctionAsync("window.__said.length >= 1");
        // The moment has passed; typing into the field that shows the error says nothing more.
        await bookTitle.FocusAsync();
        await bookTitle.PressSequentiallyAsync("ab");
        await bookTitle.FillAsync("");
        await Page.WaitForTimeoutAsync(1000);
        var said = await Page.EvaluateAsync<string>("JSON.stringify(window.__said)");
        Assert.Equal("""[{"text":"Book title is required","priority":"normal"}]""", said);
    }
}
