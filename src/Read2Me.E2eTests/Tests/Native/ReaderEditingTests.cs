using Microsoft.Playwright;
using Read2Me.E2eTests.Infrastructure;

namespace Read2Me.E2eTests.Tests.Native;

/// <summary>
/// The native reader's edit chain end to end (native-web 24, from the Angular class of the same
/// name): a paragraph row's node menu, the text prompt, the command, and the reader reloading from
/// its own receipt. Splitting a chapter is the gesture that touches all of it — the tree, the
/// chapter headers and the rows. Runs in Chromium and Firefox.
/// </summary>
[Collection(E2eCollection.Name)]
public class ReaderEditingTests(E2eAppFixture app, PlaywrightFixture pw) : E2eTestBase(app, pw)
{
    protected override WebApp WebApp => WebApp.Native;

    [Fact]
    public async Task Split_chapter_from_the_paragraph_menu_reloads_tree_and_rows_without_a_toast()
    {
        await App.SeedProjectAsync("native-split", "Native Split Book", "A. Author");

        await GotoAppAsync("projects/native-split/book?mode=speakers");

        var paragraphs = Page.Locator(".r2m-paragraph");
        await Expect(paragraphs).ToHaveCountAsync(3);
        await Expect(Page.Locator(".tree__title")).ToHaveCountAsync(1);

        // Row menus reveal on hover; the trigger is the menu's only button.
        var menu = paragraphs.Nth(1).Locator(".r2m-paragraph__menu");
        await menu.HoverAsync();
        await menu.Locator("button").ClickAsync();
        await Page.Locator("[role='menu'] [data-entry='split']").ClickAsync();

        var prompt = Page.Locator("r2m-text-prompt-dialog");
        await Expect(prompt).ToBeVisibleAsync();
        await prompt.Locator(".r2m-text-prompt-dialog__input").FillAsync("Second");
        await prompt.Locator(".r2m-text-prompt-dialog__confirm").ClickAsync();

        // Own receipt: the tree gains the new chapter and the reader shows both headers, silently.
        await Expect(Page.Locator(".tree__title")).ToHaveTextAsync(["ch1", "Second"]);
        await Expect(Page.Locator(".book__chapter")).ToHaveTextAsync(["ch1", "Second"]);
        await Expect(Page.Locator(".r2m-toast")).ToHaveCountAsync(0);

        // The host agrees.
        var overview = await Page.APIRequest.GetAsync($"{App.BaseUrl}/api/projects/native-split/book");
        Assert.Contains("\"totalChapters\":2", await overview.TextAsync());
    }
}
