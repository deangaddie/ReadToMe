using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Read2Me.E2eTests.Infrastructure;

namespace Read2Me.E2eTests.Tests.Web;

/// <summary>
/// The web projects shelf (Angular ticket 08): a seeded card renders, a project is created from
/// the dialog with a text upload and lands on the shelf, and delete removes it from the host.
/// </summary>
[Collection(E2eCollection.Name)]
public class ProjectsShelfTests(E2eAppFixture app, PlaywrightFixture pw) : WebE2eTestBase(app, pw)
{
    [Fact]
    public async Task Shelf_shows_the_seeded_card_and_a_created_project_until_it_is_deleted()
    {
        await App.SeedProjectAsync("web-shelf", "Web Shelf Book", "Shelby Author");

        await GotoAppAsync("/app/projects");

        var seeded = Page.Locator("r2m-project-card[data-folder='web-shelf']");
        await Expect(seeded).ToBeVisibleAsync();
        await Expect(seeded).ToContainTextAsync("Web Shelf Book");
        await Expect(seeded).ToContainTextAsync("Shelby Author");

        // Create: the dialog needs title, author and a book file; project title follows the book title.
        await Page.Locator(".projects__new").First.ClickAsync();
        var dialog = Page.Locator("mat-dialog-container", new() { HasText = "New project" });
        await dialog.Locator("input[name='bookTitle']").FillAsync("Shelf Upload");
        await Expect(dialog.Locator("input[name='title']")).ToHaveValueAsync("Shelf Upload");
        await dialog.Locator("input[name='author']").FillAsync("U. Loader");
        await dialog.Locator(".r2m-file-drop__input").SetInputFilesAsync(new FilePayload
        {
            Name = "shelf-upload.txt",
            MimeType = "text/plain",
            Buffer = "Chapter 1\n\nA very short book.\n"u8.ToArray(),
        });
        await Expect(dialog.Locator("r2m-status-chip", new() { HasText = "shelf-upload.txt" })).ToBeVisibleAsync();
        await dialog.Locator(".new-project__create").ClickAsync();

        // Creating opens the new project's overview; back on the shelf the card is there.
        await Assertions.Expect(Page).ToHaveURLAsync(new Regex("/app/projects/[^/]+$"), new() { Timeout = 15_000 });
        await Page.GotoAsync("/app/projects");
        var created = Page.Locator("r2m-project-card", new() { HasText = "Shelf Upload" });
        await Expect(created).ToBeVisibleAsync();
        var folder = await created.GetAttributeAsync("data-folder");
        Assert.False(string.IsNullOrEmpty(folder));

        // Delete through the card menu, confirmed; the host no longer lists the folder.
        await created.GetByRole(AriaRole.Button, new() { Name = "Actions for Shelf Upload" }).ClickAsync();
        await Page.Locator(".mat-mdc-menu-panel .r2m-project-card__delete").ClickAsync();
        var confirm = Page.Locator(".r2m-confirm-dialog");
        await Expect(confirm).ToContainTextAsync("Shelf Upload");
        await confirm.Locator(".r2m-confirm-dialog__confirm").ClickAsync();
        await Expect(created).ToHaveCountAsync(0, new() { Timeout = 10_000 });
        await Expect(seeded).ToBeVisibleAsync();

        var list = await Page.APIRequest.GetAsync($"{App.BaseUrl}/api/projects");
        Assert.DoesNotContain($"\"{folder}\"", await list.TextAsync());
    }
}
