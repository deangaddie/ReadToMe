using Microsoft.Playwright;
using Read2Me.E2eTests.Infrastructure;

namespace Read2Me.E2eTests.Tests.Web;

/// <summary>
/// Throwaway proof for #500: dialogs fit a 320 px viewport, keep their width at 1280 px, and the
/// full-screen dialog and the sheet are sized in dvh. The first three cases are the issue's; New
/// project and New theme are two whose own <c>vw</c> widths had to follow the narrower content box.
/// </summary>
public class DialogSizingProofTests(E2eAppFixture app, PlaywrightFixture pw) : E2eTestBase(app, pw)
{
    // The widths at 1280 are today's, measured on main 0d85d48b.
    [Theory]
    [InlineData("confirm", "book", "reread", ".r2m-confirm-dialog__confirm", 608)]
    [InlineData("manual-reread", "book", "manual-reread", ".manual__submit", 608)]
    [InlineData("add-titles", "book", "add-titles", "[data-action=apply-titles]", 480.37)]
    [InlineData("new-project", "projects", "New project", ".new-project__create", 608)]
    [InlineData("new-theme", "settings/themes", "New theme", ".editor__save", 608)]
    public async Task A_dialog_fits_320_px_and_keeps_its_width_at_1280(
        string name, string page, string opener, string primary, double todayWidth)
    {
        if (page == "book")
        {
            await App.SeedLongBookAsync($"proof-{name}", "Emma", "Jane Austen", chapters: 3, paragraphs: 2);
            page = $"projects/proof-{name}/book";
        }
        await Page.SetViewportSizeAsync(1280, 800);
        await GotoAppAsync(page);

        var dialog = await OpenAsync(page, opener);
        var wide = await dialog.EvaluateAsync<double>("d => d.getBoundingClientRect().width");
        await Page.Keyboard.PressAsync("Escape");
        await Expect(Page.Locator("dialog.r2m-dialog")).ToHaveCountAsync(0);

        await Page.SetViewportSizeAsync(320, 640);
        dialog = await OpenAsync(page, opener);
        var narrow = await dialog.EvaluateAsync<double[]>(
            """
            (d, primary) => {
              const r = d.getBoundingClientRect(), b = d.querySelector(primary).getBoundingClientRect();
              const c = d.querySelector('.r2m-dialog__content');
              return [r.left, r.right, r.width, b.left, b.right, b.bottom, d.scrollWidth - d.clientWidth, c.scrollWidth - c.clientWidth];
            }
            """, primary);
        var report = $"{name}: 1280 width={wide}; 320 dialog {narrow[0]}..{narrow[1]} (w={narrow[2]}), primary {narrow[3]}..{narrow[4]} bottom={narrow[5]}, dialog x-overflow={narrow[6]}, content x-overflow={narrow[7]}";
        TestContext.Current.TestOutputHelper!.WriteLine(report);

        Assert.True(narrow[0] >= 0 && narrow[1] <= 320, report);
        Assert.True(narrow[3] >= narrow[0] && narrow[4] <= narrow[1], report);
        Assert.True(narrow[6] <= 0, report);
        Assert.True(Math.Abs(wide - todayWidth) < 1, report);
    }

    [Fact]
    public async Task The_full_screen_dialog_is_the_dynamic_viewport_height()
    {
        await App.SeedLongBookAsync("proof-fullscreen", "Emma", "Jane Austen", chapters: 3, paragraphs: 2);
        await Page.SetViewportSizeAsync(320, 640);
        await GotoAppAsync("projects/proof-fullscreen/book");
        var dialog = await OpenAsync("projects/proof-fullscreen/book", "edit-with-ai");
        await Expect(dialog).ToHaveClassAsync("r2m-dialog r2m-dialog--fullscreen");
        var sizes = await dialog.EvaluateAsync<double[]>(
            "d => [parseFloat(getComputedStyle(d).height), innerHeight, d.getBoundingClientRect().width, innerWidth]");
        TestContext.Current.TestOutputHelper!.WriteLine(
            $"fullscreen: height={sizes[0]} innerHeight={sizes[1]} width={sizes[2]} innerWidth={sizes[3]}");
        Assert.Equal(sizes[1], sizes[0], 1);
        Assert.Equal(sizes[3], sizes[2], 1);

        // Computed heights are px, so the unit is read from the adopted rules.
        var rules = await Page.EvaluateAsync<string[]>(
            """
            () => {
              const sheets = [...document.styleSheets, ...document.adoptedStyleSheets];
              const rules = sheets.flatMap((s) => [...s.cssRules]);
              const of = (selector) => rules.find((r) => r.selectorText === selector).style;
              const full = of('dialog.r2m-dialog--fullscreen'), sheet = of('dialog.r2m-dialog--sheet');
              return [full.height, full.maxHeight, sheet.maxHeight];
            }
            """);
        TestContext.Current.TestOutputHelper!.WriteLine($"declared: {string.Join(", ", rules)}");
        Assert.Equal(["100dvh", "100dvh", "80dvh"], rules);
    }

    /// <summary>On the book page <paramref name="opener"/> is a Book actions entry; elsewhere a button's name.</summary>
    private async Task<ILocator> OpenAsync(string page, string opener)
    {
        if (page.EndsWith("/book"))
        {
            await Page.GetByTestId("book-actions").ClickAsync();
            await Page.Locator($"[data-action='{opener}']").First.ClickAsync();
        }
        else
        {
            await Page.GetByRole(AriaRole.Button, new() { Name = opener }).First.ClickAsync();
        }
        var dialog = Page.Locator("dialog.r2m-dialog");
        await Expect(dialog).ToBeVisibleAsync();
        return dialog;
    }
}
