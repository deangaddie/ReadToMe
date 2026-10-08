using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Read2Me.E2eTests.Infrastructure;

namespace Read2Me.E2eTests.Tests.Native;

/// <summary>
/// The native reader's modes (native-web 22, from the Angular class of the same name): the mode
/// toggle drives the URL and the row shape, a deep link opens in the named mode, and a title edit
/// from the tree's node menu (native-web 23) lands in the host and comes back through the reader's
/// own receipt. The Speakers-mode tree checkboxes are native-web 25. Runs in Chromium and Firefox.
/// </summary>
[Collection(E2eCollection.Name)]
public class ReaderModesTests(E2eAppFixture app, PlaywrightFixture pw) : E2eTestBase(app, pw)
{
    protected override WebApp WebApp => WebApp.Native;

    [Fact]
    public async Task Mode_toggle_round_trips_the_url_and_the_row_shape()
    {
        await App.SeedProjectAsync("native-modes", "Native Modes Book", "A. Author");

        await GotoAppAsync("projects/native-modes/book");

        // Read mode: plain paragraphs, no item rows, no speaker chips per item.
        var toggle = Page.GetByRole(AriaRole.Radiogroup, new() { Name = "Reader mode" });
        await Expect(Page.Locator(".r2m-paragraph")).ToHaveCountAsync(3);
        await Expect(Page.Locator(".r2m-paragraph .r2m-speaker-chip")).ToHaveCountAsync(3);
        await Expect(Page.Locator(".r2m-item .r2m-speaker-chip")).ToHaveCountAsync(0);

        // Speakers: one item row per item with its own speaker chip; the URL names the mode.
        await toggle.GetByText("Speakers").ClickAsync();
        await Assertions.Expect(Page).ToHaveURLAsync(new Regex(@"mode=speakers$"));
        await Expect(Page.Locator(".r2m-item .r2m-speaker-chip")).ToHaveCountAsync(3);
        await Expect(Page.Locator(".r2m-item--unknown")).ToHaveCountAsync(1);

        // Audio: per-item rows carry the voice preview instead.
        await toggle.GetByText("Audio").ClickAsync();
        await Assertions.Expect(Page).ToHaveURLAsync(new Regex(@"mode=audio$"));
        await Expect(Page.Locator(".r2m-item .r2m-item__voice").First).ToBeVisibleAsync();

        // Back to Read drops the query; a reload keeps whatever the URL says.
        await toggle.GetByText("Read").ClickAsync();
        await Assertions.Expect(Page).Not.ToHaveURLAsync(new Regex("mode="));
        await Page.GotoAsync(AppPath("projects/native-modes/book?mode=audio"));
        await Expect(Page.Locator(".r2m-item .r2m-item__voice").First).ToBeVisibleAsync();
        await Expect(toggle.GetByRole(AriaRole.Radio, new() { Name = "Audio" })).ToBeCheckedAsync();
    }

    [Fact]
    public async Task Edit_title_from_the_tree_menu_updates_tree_and_reader_and_the_host()
    {
        var builder = await App.SeedProjectAsync("native-title", "Native Title Book", "A. Author");

        await GotoAppAsync("projects/native-title/book");
        await Expect(Page.Locator(".tree__title")).ToHaveTextAsync(["ch1"]);
        await Expect(Page.Locator(".book__chapter")).ToHaveTextAsync(["ch1"]);

        var node = Page.Locator(".tree__node").First;
        await node.HoverAsync();
        await node.Locator(".r2m-node-menu__trigger").ClickAsync();
        await Page.Locator("[role='menu'] [data-entry='edit-title']").ClickAsync();

        var prompt = Page.Locator("r2m-text-prompt-dialog");
        await Expect(prompt.Locator(".r2m-text-prompt-dialog__input")).ToHaveValueAsync("ch1");
        await prompt.Locator(".r2m-text-prompt-dialog__input").FillAsync("Chapter the First");
        await prompt.Locator(".r2m-text-prompt-dialog__confirm").ClickAsync();

        await Expect(Page.Locator(".tree__title")).ToHaveTextAsync(["Chapter the First"]);
        await Expect(Page.Locator(".book__chapter")).ToHaveTextAsync(["Chapter the First"]);
        // The reader's own receipt reloads it: no toast for a change made here.
        await Expect(Page.Locator(".r2m-toast")).ToHaveCountAsync(0);

        // The host agrees: the volume's one (implicit) part lists the renamed chapter.
        var parts = await Page.APIRequest.GetAsync($"{App.BaseUrl}/api/projects/native-title/nodes/volume/{builder.VolumeId("v1")}/children");
        using var doc = JsonDocument.Parse(await parts.TextAsync());
        var partId = doc.RootElement.GetProperty("parts")[0].GetProperty("id").GetString();
        var chapters = await Page.APIRequest.GetAsync($"{App.BaseUrl}/api/projects/native-title/nodes/part/{partId}/children");
        Assert.Contains("Chapter the First", await chapters.TextAsync());
    }
}
