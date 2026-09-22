using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using Read2Me.Core.Models;
using Read2Me.E2eTests.Infrastructure;
using Read2Me.E2eTests.Infrastructure.FakeAi;
using Read2Me.Services.Audio;

namespace Read2Me.E2eTests.Tests.Web;

/// <summary>
/// The audio-processing settings page (Angular ticket 24) against the real host: change the
/// silence-trim threshold, audition it on a recent sample through the A/B preview, save it, and
/// read the new value back on Blazor's page.
/// </summary>
[Collection(E2eCollection.Name)]
public class AudioProcessingSettingsTests(E2eAppFixture app, PlaywrightFixture pw) : WebE2eTestBase(app, pw)
{
    private static readonly HttpClient Http = new();

    private ILocator TrimCard => Page.Locator("[data-card='silence-trim']");
    private ILocator Threshold => TrimCard.Locator("[data-field='trim-threshold']");
    private ILocator Save => TrimCard.Locator("[data-action='save']");

    private async Task<JsonElement> TrimStepAsync() =>
        JsonDocument.Parse(await Http.GetStringAsync($"{App.BaseUrl}/api/settings/audio-processing/full"))
            .RootElement.GetProperty("steps").EnumerateArray()
            .Single(s => s.GetProperty("stepId").GetString() == AudioPostProcessStepIds.SilenceTrim);

    [Fact]
    public async Task Trim_threshold_preview_on_a_recent_sample_save_and_Blazor_shows_it()
    {
        var folder = $"web-audio-{Guid.NewGuid():N}";
        var builder = await App.SeedProjectAsync(folder, "Audio Settings Book", "Author");
        var itemId = builder.ItemId("n1");
        await App.SeedItemAudioAsync(folder, itemId, builder.CharacterId("Alice"));
        using (var scope = App.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IPreviewSourceCache>()
                .SaveAsync(new ProjectFolderId(folder), itemId, FakeAiResponses.SilentWav());
        }

        var original = (await TrimStepAsync()).GetRawText();
        try
        {
            await GotoAppAsync("/app/settings/audio");
            await Expect(Page.Locator("[data-card]")).ToHaveCountAsync(7);
            await Expect(Threshold).ToHaveValueAsync("-50");
            await Expect(Save).ToBeDisabledAsync();

            await Threshold.FillAsync("-42");
            await Expect(Save).ToBeEnabledAsync();

            // Pick the seeded sample and render the unsaved draft over it.
            var preview = TrimCard.Locator("app-step-preview");
            await preview.Locator("[data-action='pick-sample']").ClickAsync();
            var row = Page.Locator($"app-sample-picker-dialog [data-item='{itemId:D}']");
            await Expect(row).ToContainTextAsync("Audio Settings Book");
            await row.ClickAsync();
            await Expect(preview.Locator("[data-role='sample']")).ToContainTextAsync("Alice");

            await preview.Locator("[data-action='render-preview']").ClickAsync();
            var ab = preview.Locator(".r2m-audio-player__ab-btn");
            await Expect(ab).ToHaveCountAsync(2);
            await Expect(ab.Nth(1)).ToHaveTextAsync("Trimmed");
            // No ffmpeg on the test host: the step falls back and says so; both players still play.
            await Expect(preview.Locator("[data-role='reason']")).ToBeVisibleAsync();
            await Expect(preview.Locator("audio")).ToHaveAttributeAsync("src", new System.Text.RegularExpressions.Regex("^/preview-source/"));

            // Previewing saved nothing.
            Assert.Equal(-50, (await TrimStepAsync()).GetProperty("settings").GetProperty("thresholdDb").GetDouble());

            await Save.ClickAsync();
            // Save disables while the write is in flight; the dirty marker clears once the reload lands.
            await Expect(TrimCard.Locator(".audio-card__dirty")).ToHaveCountAsync(0);
            await Expect(Save).ToBeDisabledAsync();
            Assert.Equal(-42, (await TrimStepAsync()).GetProperty("settings").GetProperty("thresholdDb").GetDouble());

            // Blazor's page shows the new value.
            await GotoAsync("/audio-processing-settings");
            await Expect(Page.GetByLabel("Silence threshold (dB)")).ToHaveValueAsync("-42");
        }
        finally
        {
            await Http.PutAsync($"{App.BaseUrl}/api/settings/audio-processing/steps/{AudioPostProcessStepIds.SilenceTrim}",
                new StringContent(original, System.Text.Encoding.UTF8, "application/json"));
        }
    }
}
