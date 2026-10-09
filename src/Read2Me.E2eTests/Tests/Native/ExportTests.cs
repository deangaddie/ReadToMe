using Microsoft.Playwright;
using Read2Me.E2eTests.Infrastructure;

namespace Read2Me.E2eTests.Tests.Native;

/// <summary>
/// The native export page (native-web 33, moved from the Angular ticket 20 suite) over real assembly runs, with ffmpeg faked at the encoder
/// seam: phases and encode progress arrive over the hub, the finished file lands in Outputs and
/// downloads, missing audio offers a partial build, and Cancel mid-encode shows Cancelled. Runs in Chromium and Firefox.
/// </summary>
[Collection(E2eCollection.Name)]
public class ExportTests(E2eAppFixture app, PlaywrightFixture pw) : E2eTestBase(app, pw)
{
    protected override WebApp WebApp => WebApp.Native;

    private ILocator Phase(string phase) => Page.Locator($"r2m-export-page [data-phase='{phase}']");
    private ILocator Outputs => Page.Locator("r2m-export-page [data-output]");

    [Fact]
    public async Task Assembling_a_fully_voiced_project_advances_live_and_the_output_downloads()
    {
        await App.SeedProjectAsync("native-export", "Export Book", "A. Author");
        await App.SeedAllItemAudioAsync("native-export");
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        App.Encoder.HoldAtHalf = hold;
        try
        {
            await GotoAppAsync("projects/native-export/export");
            await Expect(Page.Locator("r2m-export-page")).ToContainTextAsync("No audiobooks yet");

            await Page.Locator("[data-action='assemble']").ClickAsync();

            // Encode is reached with the earlier phases ticked off, and its percentage moves — the
            // fake encoder parks at 50% until released, so neither is a race against a timer.
            await Expect(Phase("Encode")).ToHaveAttributeAsync("data-state", "active", new() { Timeout = 10_000 });
            await Expect(Phase("Gather")).ToHaveAttributeAsync("data-state", "done");
            await Expect(Page.Locator("[data-role='encode-percent']")).ToContainTextAsync("50%", new() { Timeout = 10_000 });
            await Expect(Page.Locator("[data-action='assemble']")).ToBeDisabledAsync();
            hold.SetResult();

            await Expect(Page.Locator("[data-role='outcome']")).ToContainTextAsync("Finished: Export Book.m4b", new() { Timeout = 15_000 });
            await Expect(Phase("Finalize")).ToHaveAttributeAsync("data-state", "done");
            await Expect(Outputs).ToHaveCountAsync(1);
            await Expect(Outputs.First).ToContainTextAsync("Export Book.m4b");
            await Expect(Outputs.First).Not.ToContainTextAsync("Partial");

            var download = await Page.RunAndWaitForDownloadAsync(
                () => Page.Locator("[data-action='download-output']").ClickAsync());
            Assert.Equal("Export Book.m4b", download.SuggestedFilename);

            // A reload reads how the run ended off the hub snapshot.
            await GotoAppAsync("projects/native-export/export");
            await Expect(Page.Locator("[data-role='outcome']")).ToContainTextAsync("Finished: Export Book.m4b");

            // The overview's Export step now reports the build.
            await GotoAppAsync("projects/native-export");
            await Expect(Page.Locator("[data-step='export']")).ToContainTextAsync("Last build:");

            // Deleting asks first, then the list empties.
            await GotoAppAsync("projects/native-export/export");
            await Page.Locator("[data-action='delete-output']").ClickAsync();
            await Page.Locator("r2m-confirm-dialog .r2m-confirm-dialog__confirm").ClickAsync();
            await Expect(Outputs).ToHaveCountAsync(0);
            Assert.Empty(Directory.GetFiles(Path.Combine(App.WorkspaceDir, "native-export", "output")));
        }
        finally
        {
            App.Encoder.HoldAtHalf?.TrySetResult();
            App.Encoder.HoldAtHalf = null;
        }
    }

    [Fact]
    public async Task Missing_audio_offers_a_partial_build_that_is_marked_partial()
    {
        await App.SeedProjectAsync("native-export-partial", "Partial Book", "A. Author");

        await GotoAppAsync("projects/native-export-partial/export");
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
        await App.SeedProjectAsync("native-export-cancel", "Cancel Export", "A. Author");
        await App.SeedAllItemAudioAsync("native-export-cancel");
        App.Encoder.HoldAtHalf = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await GotoAppAsync("projects/native-export-cancel/export");
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
            App.Encoder.HoldAtHalf?.TrySetResult();
            App.Encoder.HoldAtHalf = null;
        }
    }
}
