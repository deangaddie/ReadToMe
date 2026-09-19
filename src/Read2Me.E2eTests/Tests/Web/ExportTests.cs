using Microsoft.Playwright;
using Read2Me.E2eTests.Infrastructure;

namespace Read2Me.E2eTests.Tests.Web;

/// <summary>
/// The export page (Angular ticket 20) over real assembly runs, with ffmpeg faked at the encoder
/// seam: phases and encode progress arrive over the hub, the finished file lands in Outputs and
/// downloads, missing audio offers a partial build, and Cancel mid-encode shows Cancelled.
/// </summary>
[Collection(E2eCollection.Name)]
public class ExportTests(E2eAppFixture app, PlaywrightFixture pw) : WebE2eTestBase(app, pw)
{
    private ILocator Phase(string phase) => Page.Locator($"app-export-page [data-phase='{phase}']");
    private ILocator Outputs => Page.Locator("app-export-page [data-output]");

    [Fact]
    public async Task Assembling_a_fully_voiced_project_advances_live_and_the_output_downloads()
    {
        await App.SeedProjectAsync("web-export", "Web Export Book", "A. Author");
        await App.SeedAllItemAudioAsync("web-export");
        App.Encoder.EncodeDelay = TimeSpan.FromMilliseconds(600);
        try
        {
            await GotoAppAsync("/app/projects/web-export/export");
            await Expect(Page.Locator("app-export-page")).ToContainTextAsync("No audiobooks yet");

            await Page.Locator("[data-action='assemble']").ClickAsync();

            // Encode is reached with the earlier phases ticked off, and its percentage moves.
            await Expect(Phase("Encode")).ToHaveAttributeAsync("data-state", "active", new() { Timeout = 10_000 });
            await Expect(Phase("Gather")).ToHaveAttributeAsync("data-state", "done");
            await Expect(Page.Locator("[data-role='encode-percent']")).ToContainTextAsync("50%", new() { Timeout = 10_000 });
            await Expect(Page.Locator("[data-action='assemble']")).ToBeDisabledAsync();

            await Expect(Page.Locator("[data-role='outcome']")).ToContainTextAsync("Finished: Web Export Book.m4b", new() { Timeout = 15_000 });
            await Expect(Phase("Finalize")).ToHaveAttributeAsync("data-state", "done");
            await Expect(Outputs).ToHaveCountAsync(1);
            await Expect(Outputs.First).ToContainTextAsync("Web Export Book.m4b");
            await Expect(Outputs.First).Not.ToContainTextAsync("Partial");

            var download = await Page.RunAndWaitForDownloadAsync(
                () => Page.Locator("[data-action='download-output']").ClickAsync());
            Assert.Equal("Web Export Book.m4b", download.SuggestedFilename);

            // A reload reads how the run ended off the hub snapshot.
            await GotoAppAsync("/app/projects/web-export/export");
            await Expect(Page.Locator("[data-role='outcome']")).ToContainTextAsync("Finished: Web Export Book.m4b");

            // The overview's Export step now reports the build.
            await GotoAppAsync("/app/projects/web-export");
            await Expect(Page.Locator("[data-step='export']")).ToContainTextAsync("Last build:");

            // Deleting asks first, then the list empties.
            await GotoAppAsync("/app/projects/web-export/export");
            await Page.Locator("[data-action='delete-output']").ClickAsync();
            await Page.Locator("r2m-confirm-dialog .r2m-confirm-dialog__confirm").ClickAsync();
            await Expect(Outputs).ToHaveCountAsync(0);
            Assert.Empty(Directory.GetFiles(Path.Combine(App.WorkspaceDir, "web-export", "output")));
        }
        finally
        {
            App.Encoder.EncodeDelay = TimeSpan.Zero;
        }
    }

    [Fact]
    public async Task Missing_audio_offers_a_partial_build_that_is_marked_partial()
    {
        await App.SeedProjectAsync("web-export-partial", "Web Partial Book", "A. Author");

        await GotoAppAsync("/app/projects/web-export-partial/export");
        await Page.Locator("[data-action='assemble']").ClickAsync();

        var dialog = Page.Locator("r2m-confirm-dialog");
        await Expect(dialog).ToContainTextAsync("Assemble partial");
        await Expect(dialog).ToContainTextAsync("missing audio");
        await dialog.Locator(".r2m-confirm-dialog__confirm").ClickAsync();

        await Expect(Outputs).ToHaveCountAsync(1, new() { Timeout = 15_000 });
        await Expect(Outputs.First).ToContainTextAsync("_partial_");
        await Expect(Outputs.First).ToContainTextAsync("Partial");
    }

    [Fact]
    public async Task Cancel_mid_encode_stops_the_job_and_shows_cancelled()
    {
        await App.SeedProjectAsync("web-export-cancel", "Web Cancel Export", "A. Author");
        await App.SeedAllItemAudioAsync("web-export-cancel");
        App.Encoder.EncodeDelay = TimeSpan.FromSeconds(5);
        try
        {
            await GotoAppAsync("/app/projects/web-export-cancel/export");
            await Page.Locator("[data-action='assemble']").ClickAsync();
            await Expect(Phase("Encode")).ToHaveAttributeAsync("data-state", "active", new() { Timeout = 10_000 });

            await Page.Locator("[data-action='cancel-assembly']").ClickAsync();

            await Expect(Page.Locator("[data-role='outcome']")).ToContainTextAsync("Cancelled", new() { Timeout = 10_000 });
            await Expect(Phase("Encode")).ToHaveAttributeAsync("data-state", "cancelled");
            await Expect(Page.Locator("[data-action='assemble']")).ToBeEnabledAsync();
            await Expect(Outputs).ToHaveCountAsync(0);
        }
        finally
        {
            App.Encoder.EncodeDelay = TimeSpan.Zero;
        }
    }
}
