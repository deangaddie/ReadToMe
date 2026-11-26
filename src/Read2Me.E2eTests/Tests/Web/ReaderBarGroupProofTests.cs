using Microsoft.Playwright;
using Read2Me.E2eTests.Infrastructure;

namespace Read2Me.E2eTests.Tests.Web;

/// <summary>Throwaway proof for #471: the reader bar is a group named "Reader", not a toolbar.</summary>
public class ReaderBarGroupProofTests(E2eAppFixture app, PlaywrightFixture pw) : E2eTestBase(app, pw)
{
    [Fact]
    public async Task The_reader_bar_is_a_group_named_Reader_and_no_toolbar()
    {
        await App.SeedProjectAsync("reader-bar-group", "Bar Book", "A. Author");
        await GotoAppAsync("projects/reader-bar-group/book");

        var bar = Page.GetByRole(AriaRole.Group, new() { Name = "Reader", Exact = true });
        await Expect(bar).ToBeVisibleAsync();
        await Expect(Page.GetByRole(AriaRole.Toolbar)).ToHaveCountAsync(0);

        // The browser's accessibility tree, read from the page body.
        var tree = await Page.Locator("body").AriaSnapshotAsync();
        Console.WriteLine(tree);
        Assert.Contains("- group \"Reader\"", tree);
        Assert.DoesNotContain("toolbar", tree);
        Assert.Contains("- radiogroup \"Reader mode\"", await bar.AriaSnapshotAsync());
    }
}
