using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using Read2Me.App.Shared;
using Read2Me.AppData.Entities;
using Read2Me.E2eTests.Infrastructure;
using Read2Me.Services;

namespace Read2Me.E2eTests.Tests.Web;

/// <summary>
/// The four provider settings pages (Angular ticket 22) against the real host. "Round-trips with
/// Blazor" is checked with Blazor's own config forms: what the Angular page stores is exactly what
/// the Blazor dialog would write back, and a config the Blazor form built survives an Angular save
/// with its <c>settingsJson</c> unchanged.
/// </summary>
[Collection(E2eCollection.Name)]
public class ProviderSettingsTests(E2eAppFixture app, PlaywrightFixture pw) : WebE2eTestBase(app, pw)
{
    private static readonly HttpClient Http = new();

    private ILocator Row(string name) => Page.Locator(".r2m-config-list__row", new()
    {
        Has = Page.Locator(".r2m-config-list__name", new() { HasTextRegex = new Regex($"^{Regex.Escape(name)}$") }),
    });
    private ILocator Field(string field) => Page.Locator($"app-provider-config-editor [data-field='{field}']");
    private ILocator Setting(string key) => Page.Locator($"app-provider-config-editor [data-key='{key}']");
    private ILocator Save => Page.Locator("r2m-config-editor-frame button", new() { HasText = "Save" });
    private ILocator Dirty => Page.Locator(".r2m-config-editor-frame__dirty");

    private static string NewName(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..16];

    private async Task RowActionAsync(string name, string action)
    {
        await Page.Locator($"[aria-label='Actions for {name}']").ClickAsync();
        await Page.Locator(".mat-mdc-menu-item", new() { HasText = action }).ClickAsync();
    }

    private async Task<JsonElement> StoredAsync(string area, string name) =>
        JsonDocument.Parse(await Http.GetStringAsync($"{App.BaseUrl}/api/settings/{area}")).RootElement
            .EnumerateArray().Single(c => c.GetProperty("name").GetString() == name);

    private async Task<T> StoredAsync<T>(string area, string name) =>
        (await StoredAsync(area, name)).Deserialize<T>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    private async Task DeleteAsync(string area, string name)
    {
        var all = JsonDocument.Parse(await Http.GetStringAsync($"{App.BaseUrl}/api/settings/{area}")).RootElement;
        foreach (var c in all.EnumerateArray().Where(c => c.GetProperty("name").GetString() == name))
            await Http.DeleteAsync($"{App.BaseUrl}/api/settings/{area}/{c.GetProperty("id").GetInt32()}");
    }

    private async Task NewConfigAsync(string name, string baseUrl)
    {
        await Page.Locator("r2m-config-list button", new() { HasText = "New" }).ClickAsync();
        await Expect(Page.Locator(".r2m-config-editor-frame__title")).ToHaveTextAsync("New configuration");
        await Field("name").FillAsync(name);
        await Field("baseUrl").FillAsync(baseUrl);
    }

    // ── TTS ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Tts_config_with_text_processing_saves_reloads_and_matches_what_Blazor_writes()
    {
        var name = NewName("web-tts");
        try
        {
            await GotoAppAsync("/app/settings/tts");
            await Expect(Row("fake")).ToContainTextAsync("Active");

            await NewConfigAsync(name, "nowhere");
            await Save.ClickAsync();
            await Expect(Page.Locator(".provider-editor__error")).ToContainTextAsync("Base URL must be a valid absolute URL");
            await Field("baseUrl").FillAsync("http://example-tts:8003");

            await Setting("maxChunkChars").Locator("input").FillAsync("350");
            await Setting("carrierPrefixEnabled").Locator("button").ClickAsync();
            await Setting("carrierMaxTargetChars").Locator("input").FillAsync("25");
            await Setting("cfg_value").Locator("input[type=number]").FillAsync("3.5");

            await Page.Locator("[data-step='to-sentence-case'] input").CheckAsync();
            await Page.Locator("[data-option='wordMinLength']").FillAsync("8");
            await Page.Locator("[data-action='add-substitution']").ClickAsync();
            var substitution = Page.Locator("[data-role='substitution']");
            await substitution.Locator("[data-field='fromText']").FillAsync("Dr.");
            await substitution.Locator("[data-field='toText']").FillAsync("Doctor");
            await Save.ClickAsync();

            await Expect(Row(name)).ToContainTextAsync("http://example-tts:8003");
            await Expect(Dirty).ToHaveCountAsync(0);

            var stored = await StoredAsync<ParagraphTtsServiceConfig>("paragraph-tts", name);
            Assert.Equal(ParagraphTtsServiceConfigForm.FromConfig(stored).BuildConfig().SettingsJson, stored.SettingsJson);
            Assert.Contains("\"maxChunkChars\":350", stored.SettingsJson);
            Assert.Contains("\"carrierMaxTargetChars\":25", stored.SettingsJson);
            Assert.Contains("\"cfg_value\":3.5", stored.SettingsJson);
            var step = Assert.Single(stored.SubstitutionSteps);
            Assert.Equal(("Dr.", "Doctor"), (step.FromText, step.ToText));
            Assert.Equal(["to-sentence-case", step.Id], stored.EnabledStepIds);
            Assert.Equal(8, stored.ToSentenceCaseConfig!.WordMinLength);

            // A fresh page shows what was saved.
            await GotoAppAsync("/app/settings/tts");
            await Row(name).Locator(".r2m-config-list__main").ClickAsync();
            await Expect(substitution.Locator("[data-field='toText']")).ToHaveValueAsync("Doctor");
            await Expect(Page.Locator("[data-step='to-sentence-case'] input")).ToBeCheckedAsync();
            await Expect(Page.Locator("[data-option='wordMinLength']")).ToHaveValueAsync("8");
            await Expect(Setting("carrierMaxTargetChars").Locator("input")).ToHaveValueAsync("25");

            // Disabling the substitution keeps its row.
            await substitution.Locator("[data-field='enabled'] input").UncheckAsync();
            await Save.ClickAsync();
            await Expect(Dirty).ToHaveCountAsync(0);
            stored = await StoredAsync<ParagraphTtsServiceConfig>("paragraph-tts", name);
            Assert.Equal(["to-sentence-case"], stored.EnabledStepIds);
            Assert.Single(stored.SubstitutionSteps);

            await RowActionAsync(name, "Delete");
            await Page.Locator("r2m-confirm-dialog .r2m-confirm-dialog__confirm").ClickAsync();
            await Expect(Row(name)).ToHaveCountAsync(0);
        }
        finally
        {
            await DeleteAsync("paragraph-tts", name);
        }
    }

    [Fact]
    public async Task A_config_the_Blazor_form_built_keeps_its_settingsJson_through_an_Angular_save()
    {
        var name = NewName("blazor-vd");
        var renamed = NewName("renamed-vd");
        try
        {
            var built = new VoiceDesignServiceConfigForm
            {
                Name = name,
                Type = VoiceDesignServiceType.Qwen3,
                BaseUrl = "http://example-qwen:8100",
                ApiKey = "k+1",
                Language = "en",
                TopK = 40,
            }.BuildConfig();
            using (var scope = App.Services.CreateScope())
                await scope.ServiceProvider.GetRequiredService<VoiceDesignSettingsService>().CreateConfigAsync(built);

            await GotoAppAsync("/app/settings/voice-design");
            await Row(name).Locator(".r2m-config-list__main").ClickAsync();
            await Expect(Setting("language")).ToBeVisibleAsync();
            await Expect(Setting("topK").Locator("input")).ToHaveValueAsync("40");
            await Expect(Setting("apiKey").Locator("input")).ToHaveAttributeAsync("type", "password");

            await Field("name").FillAsync(renamed);
            await Save.ClickAsync();
            await Expect(Row(renamed)).ToBeVisibleAsync();

            Assert.Equal(built.SettingsJson, (await StoredAsync("voice-design", renamed)).GetProperty("settingsJson").GetString());
        }
        finally
        {
            await DeleteAsync("voice-design", name);
            await DeleteAsync("voice-design", renamed);
        }
    }

    // ── voice design ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Voice_design_saves_its_sample_text_and_plays_a_test_design()
    {
        try
        {
            await GotoAppAsync("/app/settings/voice-design");
            var sample = Page.Locator("app-voice-design-sample-text");
            var saveSample = sample.Locator("[data-action='save-sample-text']");
            await Expect(saveSample).ToBeDisabledAsync();

            await sample.Locator("[data-field='sampleText']").FillAsync("A sentence of my own.");
            await saveSample.ClickAsync();
            await Expect(saveSample).ToBeDisabledAsync();
            var stored = JsonDocument.Parse(await Http.GetStringAsync($"{App.BaseUrl}/api/settings/voice-design/sample-text")).RootElement;
            Assert.Equal("A sentence of my own.", stored.GetProperty("text").GetString());

            await sample.Locator("[data-action='reset-sample-text']").ClickAsync();
            await Expect(sample.Locator("[data-field='sampleText']")).ToHaveValueAsync(stored.GetProperty("default").GetString()!);
            await saveSample.ClickAsync();
            await Expect(saveSample).ToBeDisabledAsync();

            var test = Page.Locator("app-voice-design-test");
            await Expect(test).ToContainTextAsync("Test \"fake\"");
            await test.Locator("[data-field='prompt']").FillAsync("A warm old man");
            await test.Locator("[data-action='test']").ClickAsync();
            await Expect(test.Locator("r2m-audio-player")).ToBeVisibleAsync(new() { Timeout = 15_000 });
        }
        finally
        {
            await Http.PutAsJsonAsync($"{App.BaseUrl}/api/settings/voice-design/sample-text", new { text = (string?)null });
        }
    }

    // ── transcription ────────────────────────────────────────────────────────

    [Fact]
    public async Task Transcription_test_shows_the_transcript_of_a_dropped_file()
    {
        await GotoAppAsync("/app/settings/transcription");
        var test = Page.Locator("app-transcription-test");
        await Expect(test).ToContainTextAsync("Test \"fake\"");

        await test.Locator("input[type=file]").SetInputFilesAsync(new FilePayload
        {
            Name = "clip.wav",
            MimeType = "audio/wav",
            Buffer = [1, 2, 3, 4],
        });

        await Expect(test.Locator("[data-role='transcript']")).ToContainTextAsync("Transcript", new() { Timeout = 15_000 });
    }

    // ── similarity ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Similarity_config_round_trips_with_Blazor_and_its_test_reports_pass_and_failure()
    {
        var name = NewName("web-sim");
        try
        {
            await GotoAppAsync("/app/settings/similarity");
            await NewConfigAsync(name, "http://no-such-similarity");

            await Setting("PassThreshold").Locator("input[type=number]").FillAsync("1");
            await Save.ClickAsync();
            await Expect(Page.Locator(".provider-editor__error")).ToHaveTextAsync("Pass threshold must be between 0 and 1 (exclusive).");
            await Setting("PassThreshold").Locator("input[type=number]").FillAsync("0.7");
            await Save.ClickAsync();
            await Expect(Row(name)).ToContainTextAsync("threshold 0.70");

            var stored = await StoredAsync<SemanticSimilarityServiceConfig>("semantic-similarity", name);
            Assert.Equal(SemanticSimilarityServiceConfigForm.FromConfig(stored).BuildConfig().SettingsJson, stored.SettingsJson);
            Assert.Equal("""{"BaseUrl":"http://no-such-similarity","PassThreshold":0.7}""", stored.SettingsJson);

            // The new config's server does not exist: the failure is reported, not thrown.
            var test = Page.Locator("app-similarity-test");
            await Expect(test).ToContainTextAsync($"Test \"{name}\"");
            await test.Locator("[data-field='text1']").FillAsync("The cat sat.");
            await test.Locator("[data-field='text2']").FillAsync("A cat was sitting.");
            await test.Locator("[data-action='test']").ClickAsync();
            await Expect(test).ToContainTextAsync("Test failed:", new() { Timeout = 15_000 });

            // The seeded one answers.
            await Row("fake").Locator(".r2m-config-list__main").ClickAsync();
            await Expect(test).ToContainTextAsync("Test \"fake\"");
            await test.Locator("[data-field='text1']").FillAsync("The cat sat.");
            await test.Locator("[data-field='text2']").FillAsync("A cat was sitting.");
            await test.Locator("[data-action='test']").ClickAsync();
            await Expect(test.Locator("[data-role='score']")).ToContainTextAsync("PASS", new() { Timeout = 15_000 });

            await RowActionAsync(name, "Make active");
            await Expect(Row(name)).ToContainTextAsync("Active");
            await RowActionAsync("fake", "Make active");
            await Expect(Row("fake")).ToContainTextAsync("Active");
        }
        finally
        {
            await DeleteAsync("semantic-similarity", name);
        }
    }
}
