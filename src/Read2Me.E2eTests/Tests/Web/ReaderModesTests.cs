using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Read2Me.E2eTests.Infrastructure;

namespace Read2Me.E2eTests.Tests.Web;

/// <summary>
/// The web reader's modes and node menu (Angular tickets 10 and 11): the mode toggle drives the
/// URL and the row shape, a deep link opens in the named mode, and a chapter title edited from the
/// tree's node menu lands in the host and comes back through the reader's own receipt.
/// </summary>
[Collection(E2eCollection.Name)]
public class ReaderModesTests(E2eAppFixture app, PlaywrightFixture pw) : WebE2eTestBase(app, pw)
{
    [Fact]
    public async Task Mode_toggle_round_trips_the_url_and_the_row_shape()
    {
        await App.SeedProjectAsync("web-modes", "Web Modes Book", "A. Author");

        await GotoAppAsync("/app/projects/web-modes/book");

        // Read mode: plain paragraphs, no speaker chips, no selection boxes.
        var toggle = Page.Locator("mat-button-toggle-group[aria-label='Reader mode']");
        await Expect(Page.Locator("r2m-paragraph")).ToHaveCountAsync(3);
        await Expect(Page.Locator("r2m-item .r2m-speaker-chip")).ToHaveCountAsync(0);

        // Speakers: per-item speaker chips and paragraph checkboxes; the URL names the mode.
        await toggle.GetByText("Speakers").ClickAsync();
        await Assertions.Expect(Page).ToHaveURLAsync(new Regex(@"mode=speakers$"));
        await Expect(Page.Locator("r2m-item .r2m-speaker-chip")).ToHaveCountAsync(3);
        await Expect(Page.Locator("[data-node-id] .tree__select").First).ToBeVisibleAsync();

        // Audio: per-item rows carry the voice preview instead.
        await toggle.GetByText("Audio").ClickAsync();
        await Assertions.Expect(Page).ToHaveURLAsync(new Regex(@"mode=audio$"));
        await Expect(Page.Locator("r2m-item .r2m-item__voice").First).ToBeVisibleAsync();

        // Back to Read drops the query; a reload keeps whatever the URL says.
        await toggle.GetByText("Read").ClickAsync();
        await Assertions.Expect(Page).Not.ToHaveURLAsync(new Regex("mode="));
        await Page.GotoAsync("/app/projects/web-modes/book?mode=audio");
        await Expect(Page.Locator("r2m-item .r2m-item__voice").First).ToBeVisibleAsync();
    }

    [Fact]
    public async Task Edit_title_from_the_tree_menu_updates_tree_and_reader_and_the_host()
    {
        var builder = await App.SeedProjectAsync("web-title", "Web Title Book", "A. Author");

        await GotoAppAsync("/app/projects/web-title/book");
        await Expect(Page.Locator(".tree__title")).ToHaveTextAsync(["ch1"]);

        var node = Page.Locator(".tree__node").First;
        await node.HoverAsync();
        await node.Locator(".r2m-node-menu__trigger").ClickAsync();
        await Page.Locator(".mat-mdc-menu-panel [data-entry='edit-title']").ClickAsync();

        var prompt = Page.Locator("r2m-text-prompt-dialog");
        await Expect(prompt.Locator(".r2m-text-prompt-dialog__input")).ToHaveValueAsync("ch1");
        await prompt.Locator(".r2m-text-prompt-dialog__input").FillAsync("Chapter the First");
        await prompt.Locator(".r2m-text-prompt-dialog__confirm").ClickAsync();

        await Expect(Page.Locator(".tree__title")).ToHaveTextAsync(["Chapter the First"]);
        await Expect(Page.Locator(".book__chapter")).ToHaveTextAsync(["Chapter the First"]);
        await Expect(Page.Locator(".r2m-toast-panel")).ToHaveCountAsync(0);

        // The host agrees: the volume's one (implicit) part lists the renamed chapter.
        var parts = await Page.APIRequest.GetAsync($"{App.BaseUrl}/api/projects/web-title/nodes/volume/{builder.VolumeId("v1")}/children");
        using var doc = JsonDocument.Parse(await parts.TextAsync());
        var partId = doc.RootElement.GetProperty("parts")[0].GetProperty("id").GetString();
        var chapters = await Page.APIRequest.GetAsync($"{App.BaseUrl}/api/projects/web-title/nodes/part/{partId}/children");
        Assert.Contains("Chapter the First", await chapters.TextAsync());
    }
}
