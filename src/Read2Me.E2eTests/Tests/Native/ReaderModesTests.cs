using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Read2Me.E2eTests.Infrastructure;

namespace Read2Me.E2eTests.Tests.Native;

/// <summary>
/// The native reader's modes (native-web 22, from the Angular class of the same name): the mode
/// toggle drives the URL and the row shape, a deep link opens in the named mode, and a chapter
/// title changed on the host comes back through the reader's own receipt. The structure tree is
/// native-web 23, so until then the title is changed through the agent API rather than the tree's
/// node menu, and the tree assertions wait there. Runs in Chromium and Firefox.
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
    public async Task A_title_changed_on_the_host_reaches_the_reader_through_its_receipt()
    {
        var builder = await App.SeedProjectAsync("native-title", "Native Title Book", "A. Author");

        await GotoAppAsync("projects/native-title/book");
        await Expect(Page.Locator(".book__chapter")).ToHaveTextAsync(["ch1"]);

        // The tree's node menu is native-web 23; the same command goes through the agent API.
        var rename = await Page.APIRequest.PostAsync($"{App.BaseUrl}/api/projects/native-title/commands", new()
        {
            DataObject = new { type = "UpdateChapterTitle", chapterId = builder.ChapterId("ch1"), title = "Chapter the First" },
        });
        Assert.True(rename.Ok, await rename.TextAsync());

        await Expect(Page.Locator(".book__chapter")).ToHaveTextAsync(["Chapter the First"]);
        // A title change is not structural, so no "updated elsewhere" toast either.
        await Expect(Page.Locator(".r2m-toast")).ToHaveCountAsync(0);

        // The host agrees: the volume's one (implicit) part lists the renamed chapter.
        var parts = await Page.APIRequest.GetAsync($"{App.BaseUrl}/api/projects/native-title/nodes/volume/{builder.VolumeId("v1")}/children");
        using var doc = JsonDocument.Parse(await parts.TextAsync());
        var partId = doc.RootElement.GetProperty("parts")[0].GetProperty("id").GetString();
        var chapters = await Page.APIRequest.GetAsync($"{App.BaseUrl}/api/projects/native-title/nodes/part/{partId}/children");
        Assert.Contains("Chapter the First", await chapters.TextAsync());
    }
}
