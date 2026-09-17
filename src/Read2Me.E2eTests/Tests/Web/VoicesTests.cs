using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Playwright;
using Read2Me.E2eTests.Infrastructure;
using Read2Me.E2eTests.Infrastructure.FakeAi;

namespace Read2Me.E2eTests.Tests.Web;

/// <summary>
/// The Voices section of the cast page (Angular ticket 16) on the fake-AI host: a prompt voice's
/// whole life (AI prompt, generated audio, switch to reference, upload, transcribe), a TTS
/// settings override round trip, and the prompt batch landing on the cards from hub events.
/// </summary>
[Collection(E2eCollection.Name)]
public class VoicesTests(E2eAppFixture app, PlaywrightFixture pw) : WebE2eTestBase(app, pw)
{
    private static readonly HttpClient Http = new();

    private const string VoicePlanReply =
        """[ { "name": "Main Voice", "description": "the only voice", "design_prompt": "A clear adult voice." } ]""";

    private async Task<JsonElement> VoiceAsync(string folder, string voiceId) =>
        JsonDocument.Parse(await Http.GetStringAsync($"{App.BaseUrl}/api/projects/{folder}/voices/{voiceId}")).RootElement;

    private async Task<Guid> CreateVoiceAsync(string folder, Guid characterId, string name, bool isGenerated)
    {
        var response = await Http.PostAsJsonAsync($"{App.BaseUrl}/api/projects/{folder}/commands",
            new { type = "CreateVoice", characterId, name, isGenerated });
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("newEntityId").GetGuid();
    }

    private static string SilentWavFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"r2me-voice-{Guid.NewGuid():N}.wav");
        File.WriteAllBytes(path, FakeAiResponses.SilentWav());
        return path;
    }

    [Fact]
    public async Task Prompt_voice_life_cycle_ai_prompt_audio_reference_upload_transcribe()
    {
        var book = await App.SeedProjectAsync("web-voices-life", "Voices Book", "A. Author", characterName: "Alice");
        var alice = book.CharacterId("Alice");
        App.FakeAi.LlmReply = _ => "A warm, unhurried alto with a faint Scottish lilt.";

        await GotoAppAsync($"/app/projects/web-voices-life/cast/{alice}");
        var section = Page.Locator("app-voices-section");
        await Expect(section).ToBeVisibleAsync();

        // Add a prompt voice.
        await section.Locator("[data-action='add-voice']").ClickAsync();
        var add = Page.Locator("app-add-voice-dialog");
        await add.Locator("input[type='text']").FillAsync("Alice Prompt");
        await add.Locator("mat-radio-button", new() { HasText = "Prompt" }).ClickAsync();
        await add.Locator("[data-action='add']").ClickAsync();
        var card = section.Locator("app-voice-card");
        await Expect(card).ToHaveCountAsync(1);
        await Expect(card).ToContainTextAsync("Alice Prompt");
        await Expect(card.Locator("r2m-status-chip", new() { HasText = "Prompt" })).ToBeVisibleAsync();
        var voiceId = (await card.GetAttributeAsync("data-voice-id"))!;

        // Regenerate with AI: render → editable prompt dialog → send → the answer is the draft.
        await card.Locator("mat-expansion-panel-header").First.ClickAsync();
        await Expect(card.Locator("[data-action='generate-audio']")).ToBeDisabledAsync();
        await card.Locator("[data-action='regenerate-prompt']").ClickAsync();
        var gen = Page.Locator("app-generate-prompt-dialog");
        await Expect(gen.Locator("mat-dialog-content")).ToHaveAttributeAsync("data-phase", "edit", new() { Timeout = 15_000 });
        await Expect(gen.Locator("textarea")).ToHaveValueAsync(new System.Text.RegularExpressions.Regex("Alice"));
        await gen.Locator("[data-action='send']").ClickAsync();
        await Expect(gen).ToHaveCountAsync(0, new() { Timeout = 15_000 });
        var promptField = card.Locator("textarea[aria-label='Voice description prompt']");
        await Expect(promptField).ToHaveValueAsync("A warm, unhurried alto with a faint Scottish lilt.");
        await card.Locator("[data-action='save-prompt']").ClickAsync();
        await Expect(card.Locator("[data-action='save-prompt']")).ToBeDisabledAsync();
        Assert.Equal("A warm, unhurried alto with a faint Scottish lilt.",
            (await VoiceAsync("web-voices-life", voiceId)).GetProperty("designPrompt").GetString());

        // Generate audio against the fake voice-design service, then play it.
        await card.Locator("[data-action='generate-audio']").ClickAsync();
        var player = card.Locator("r2m-audio-player");
        await Expect(player).ToHaveCountAsync(1, new() { Timeout = 30_000 });
        var src = await player.Locator("audio").GetAttributeAsync("src");
        Assert.NotNull(src);
        Assert.Contains("/workspace/web-voices-life/", src);
        var audio = await Http.GetAsync($"{App.BaseUrl}{src}");
        Assert.True(audio.IsSuccessStatusCode, $"voice audio {src} → {(int)audio.StatusCode}");
        await player.Locator("button[aria-label='Play']").ClickAsync();
        await Expect(card.Locator("[data-action='generate-audio']")).ToContainTextAsync("Regenerate audio");
        await Expect(card.Locator("[data-action='edit-audio']")).ToBeVisibleAsync();

        // Switch to reference: the design prompt is dropped, so it is confirmed first.
        await card.Locator("mat-button-toggle[data-source='Uploaded'] button").ClickAsync();
        var confirm = Page.Locator("r2m-confirm-dialog");
        await Expect(confirm).ToContainTextAsync("design prompt");
        await confirm.Locator(".r2m-confirm-dialog__confirm").ClickAsync();
        await Expect(card.Locator("r2m-status-chip", new() { HasText = "Reference" })).ToBeVisibleAsync();
        await Expect(card.Locator("[data-mode='reference']")).ToBeVisibleAsync();

        // Upload a WAV through the file drop; the host normalises and commits it.
        var wav = SilentWavFile();
        try
        {
            await card.Locator(".r2m-file-drop__input").SetInputFilesAsync(wav);
            await Expect(card.Locator("r2m-file-drop")).ToContainTextAsync("Replace audio", new() { Timeout = 15_000 });
        }
        finally
        {
            File.Delete(wav);
        }
        var uploaded = await VoiceAsync("web-voices-life", voiceId);
        Assert.Equal("Uploaded", uploaded.GetProperty("source").GetString());
        Assert.Equal(JsonValueKind.Null, uploaded.GetProperty("designPrompt").ValueKind);
        Assert.False(string.IsNullOrEmpty(uploaded.GetProperty("audioFileName").GetString()));

        // Transcript: a hand edit saves (dirty-gated), then Send to AI replaces it with the fake
        // whisper's answer (which echoes the last synthesised text) in the card and the host.
        await card.Locator("[data-section='transcript'] mat-expansion-panel-header").ClickAsync();
        var transcriptField = card.Locator("textarea[aria-label='Transcript']");
        await Expect(card.Locator("[data-action='save-transcript']")).ToBeDisabledAsync();
        await transcriptField.FillAsync("hand-written transcript");
        await card.Locator("[data-action='save-transcript']").ClickAsync();
        await Expect(card.Locator("[data-action='save-transcript']")).ToBeDisabledAsync();
        Assert.Equal("hand-written transcript", (await VoiceAsync("web-voices-life", voiceId)).GetProperty("transcript").GetString());

        await card.Locator("[data-action='transcribe']").ClickAsync();
        await Expect(transcriptField).Not.ToHaveValueAsync("hand-written transcript", new() { Timeout = 15_000 });
        var transcribed = (await VoiceAsync("web-voices-life", voiceId)).GetProperty("transcript").GetString();
        Assert.False(string.IsNullOrWhiteSpace(transcribed));
        await Expect(transcriptField).ToHaveValueAsync(transcribed!);
    }

    [Fact]
    public async Task Tts_override_saves_shows_the_dot_after_reload_and_reset_restores_the_default()
    {
        var book = await App.SeedProjectAsync("web-voices-override", "Override Book", "A. Author", characterName: "Alice");
        var alice = book.CharacterId("Alice");
        var voiceId = await CreateVoiceAsync("web-voices-override", alice, "Alice Voice", isGenerated: true);

        await GotoAppAsync($"/app/projects/web-voices-override/cast/{alice}");
        var card = Page.Locator($"app-voice-card[data-voice-id='{voiceId}']");
        await card.Locator("mat-expansion-panel-header").First.ClickAsync();
        await card.Locator("[data-section='advanced'] mat-expansion-panel-header").ClickAsync();
        await card.GetByRole(AriaRole.Tab, new() { Name = "Text-to-Speech" }).ClickAsync();

        var tts = card.Locator("app-voice-override-editor[data-area='paragraph-tts']");
        var cfg = tts.Locator("[data-key='cfg_value']");
        await Expect(cfg).ToBeVisibleAsync(new() { Timeout = 15_000 });
        await Expect(cfg.Locator(".r2m-settings-form__dot--on")).ToHaveCountAsync(0);
        await Expect(tts.Locator("[data-action='save-override']")).ToBeDisabledAsync();

        await cfg.Locator(".r2m-settings-form__number").FillAsync("3.5");
        await Expect(cfg.Locator(".r2m-settings-form__dot--on")).ToHaveCountAsync(1);
        await tts.Locator("[data-action='save-override']").ClickAsync();
        await Expect(tts.Locator("[data-action='save-override']")).ToBeDisabledAsync();
        Assert.Equal("{\"cfg_value\":3.5}",
            (await VoiceAsync("web-voices-override", voiceId.ToString())).GetProperty("ttsSettingsOverrideJson").GetString());

        // Reload: the stored override shows the dot; reset restores the provider default.
        await GotoAppAsync($"/app/projects/web-voices-override/cast/{alice}");
        card = Page.Locator($"app-voice-card[data-voice-id='{voiceId}']");
        await card.Locator("mat-expansion-panel-header").First.ClickAsync();
        await card.Locator("[data-section='advanced'] mat-expansion-panel-header").ClickAsync();
        await card.GetByRole(AriaRole.Tab, new() { Name = "Text-to-Speech" }).ClickAsync();
        tts = card.Locator("app-voice-override-editor[data-area='paragraph-tts']");
        cfg = tts.Locator("[data-key='cfg_value']");
        await Expect(cfg.Locator(".r2m-settings-form__dot--on")).ToHaveCountAsync(1, new() { Timeout = 15_000 });
        await Expect(cfg.Locator(".r2m-settings-form__number")).ToHaveValueAsync("3.5");

        await cfg.Locator(".r2m-settings-form__reset").ClickAsync();
        await Expect(cfg.Locator(".r2m-settings-form__dot--on")).ToHaveCountAsync(0);
        await Expect(cfg.Locator(".r2m-settings-form__number")).ToHaveValueAsync("2");
        await tts.Locator("[data-action='save-override']").ClickAsync();
        await Expect(tts.Locator("[data-action='save-override']")).ToBeDisabledAsync();
        Assert.Equal(JsonValueKind.Null,
            (await VoiceAsync("web-voices-override", voiceId.ToString())).GetProperty("ttsSettingsOverrideJson").ValueKind);
    }

    [Fact]
    public async Task Prompt_batch_for_a_cast_of_three_lands_on_the_cards_from_hub_events()
    {
        var book = await App.SeedProjectAsync("web-voices-batch", "Batch Book", "A. Author", characterName: "Alice");
        var alice = book.CharacterId("Alice");
        foreach (var name in new[] { "Bob", "Carol" })
        {
            var created = await Http.PostAsJsonAsync($"{App.BaseUrl}/api/projects/web-voices-batch/commands",
                new { type = "CreateCharacter", name });
            created.EnsureSuccessStatusCode();
        }
        App.FakeAi.LlmReply = _ => VoicePlanReply;
        App.FakeAi.LlmDelay = TimeSpan.FromMilliseconds(1200);

        await GotoAppAsync($"/app/projects/web-voices-batch/cast/{alice}");
        await Expect(Page.Locator("app-voices-section")).ToContainTextAsync("No voices yet");
        var rows = Page.Locator(".cast__row");
        await Expect(rows).ToHaveCountAsync(4);
        await Expect(rows.Locator("r2m-status-chip")).ToHaveCountAsync(0);

        // No voices exist, so the batch starts without the scope dialog.
        await Page.Locator("[data-action='generate-prompts']").ClickAsync();
        await Expect(Page.Locator("app-voice-scope-dialog")).ToHaveCountAsync(0);
        await Expect(Page.Locator("[data-action='generate-prompts']")).ToBeDisabledAsync(new() { Timeout = 10_000 });

        // Readiness chips appear one character at a time as the batch's hub events land (the
        // Narrator is planned too, so four in all), and Alice's card arrives without any click.
        // A part-way count proves the roster refreshed mid-batch rather than once at the end.
        var chips = rows.Locator("r2m-status-chip");
        await Page.WaitForFunctionAsync(
            "() => { const n = document.querySelectorAll('.cast__row r2m-status-chip').length; return n >= 1 && n < 4; }",
            null, new() { Timeout = 20_000 });
        await Expect(chips).ToHaveCountAsync(4, new() { Timeout = 20_000 });
        var card = Page.Locator("app-voice-card");
        await Expect(card).ToHaveCountAsync(1, new() { Timeout = 15_000 });
        await Expect(card).ToContainTextAsync("Main Voice");
        await Expect(Page.Locator("[data-action='generate-prompts']")).ToBeEnabledAsync(new() { Timeout = 15_000 });

        var status = JsonDocument.Parse(await Http.GetStringAsync($"{App.BaseUrl}/api/voice-batch/status")).RootElement;
        Assert.False(status.GetProperty("isRunning").GetBoolean());
        Assert.Equal(4, status.GetProperty("processed").GetInt32());

        // Voices now exist: the next prompt batch asks for its scope, and Cancel starts nothing.
        await Page.Locator("[data-action='generate-prompts']").ClickAsync();
        var scope = Page.Locator("app-voice-scope-dialog");
        await Expect(scope).ToBeVisibleAsync();
        await Expect(scope.Locator("[data-scope='regenerate-all']")).ToContainTextAsync("Clear and regenerate all");
        await scope.Locator("mat-dialog-actions button", new() { HasText = "Cancel" }).ClickAsync();
        await Expect(scope).ToHaveCountAsync(0);
        await Expect(Page.Locator("[data-action='generate-prompts']")).ToBeEnabledAsync();
    }
}
