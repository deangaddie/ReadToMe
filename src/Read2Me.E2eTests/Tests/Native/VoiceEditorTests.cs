using Microsoft.Playwright;
using Read2Me.E2eTests.Infrastructure;

namespace Read2Me.E2eTests.Tests.Native;

/// <summary>
/// The native voice audio editor (native-web 32, moved from the Angular ticket 18 suite) end to end: tick two
/// steps, preview, hear both stages, apply, see Edited on the cast card and after a reload, restore. Asserts on
/// the <b>files</b> — <c>{voiceId}.orig.wav</c> exists ⟺ the voice has been edited. The filters
/// are ffmpeg-gated and may skip on this host, so the live bytes are not asserted to differ. Runs in Chromium and Firefox.
/// </summary>
[Collection(E2eCollection.Name)]
public class VoiceEditorTests(E2eAppFixture app, PlaywrightFixture pw) : E2eTestBase(app, pw)
{
    private static readonly HttpClient Http = new();

    protected override WebApp WebApp => WebApp.Native;

    [Fact]
    public async Task Tick_preview_apply_restore_round_trip()
    {
        const string folder = "native-voice-editor";
        var book = await App.SeedProjectAsync(folder, "Voice Editor Book", "A. Author", characterName: "Alice");
        var alice = book.CharacterId("Alice");
        var voiceId = await App.SeedEditableVoiceAsync(folder, alice);

        var voicesDir = Path.Combine(App.WorkspaceDir, folder, "voices", alice.ToString());
        var livePath = Directory.GetFiles(voicesDir, $"{voiceId}-*.wav").Single();
        var originalPath = Path.Combine(voicesDir, $"{voiceId}.orig.wav");
        var beforeBytes = await File.ReadAllBytesAsync(livePath);

        // 1. The cast card's Edit audio link lands on the editor.
        await GotoAppAsync($"projects/{folder}/cast/{alice}");
        var card = Page.Locator($"r2m-voice-card[data-voice-id='{voiceId}']");
        await card.Locator("summary.voice-card__header").ClickAsync();
        await card.Locator("[data-action='edit-audio']").ClickAsync();
        await Assertions.Expect(Page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex(AppPath($"projects/{folder}/voices/{voiceId}/editor$")));

        var editor = Page.Locator("r2m-voice-editor-page");
        var apply = editor.Locator("[data-action='apply']");
        var preview = editor.Locator("[data-action='preview']");
        await Expect(editor.Locator("[data-step]")).ToHaveCountAsync(5);
        await Expect(apply).ToBeDisabledAsync();
        await Expect(preview).ToBeDisabledAsync();

        // 2. Tick Silence trim + Denoise; Apply stays blocked until a render has been heard.
        await editor.Locator("[data-tick='silence-trim']").ClickAsync();
        await editor.Locator("[data-tick='denoise']").ClickAsync();
        await Expect(preview).ToBeEnabledAsync();
        await Expect(apply).ToBeDisabledAsync();

        // 3. Preview stacks a player per ticked step, in chain order, and each stage is playable.
        await preview.ClickAsync();
        var stages = editor.Locator("[data-stage]");
        await Expect(stages).ToHaveCountAsync(2, new() { Timeout = 30_000 });
        Assert.Equal(["denoise", "silence-trim"],
            await stages.EvaluateAllAsync<string[]>("els => els.map(e => e.getAttribute('data-stage'))"));
        foreach (var stage in await stages.AllAsync())
        {
            var src = await stage.Locator("audio").GetAttributeAsync("src");
            var wav = await Http.GetAsync($"{App.BaseUrl}{src}");
            Assert.True(wav.IsSuccessStatusCode, $"stage {src} → {(int)wav.StatusCode}");
            Assert.Equal("audio/wav", wav.Content.Headers.ContentType!.MediaType);
        }
        await Expect(apply).ToBeEnabledAsync();

        // 4. A dial edit stales the render; a new preview clears it.
        var strength = editor.Locator("[data-key='strength'] input[type='number']");
        await strength.FillAsync("31");
        await Expect(apply).ToBeDisabledAsync();
        await Expect(editor.Locator("[data-testid='stale-hint']")).ToBeVisibleAsync();
        await preview.ClickAsync();
        await Expect(apply).ToBeEnabledAsync(new() { Timeout = 30_000 });

        // 5. Apply captures the original; the editor and the card both say Edited.
        await apply.ClickAsync();
        await Expect(editor.Locator("[data-testid='edited-chip']")).ToBeVisibleAsync(new() { Timeout = 30_000 });
        Assert.True(File.Exists(originalPath));
        Assert.Equal(beforeBytes, await File.ReadAllBytesAsync(originalPath));

        await editor.Locator("[data-action='back']").ClickAsync();
        await Expect(card.Locator("[data-testid='voice-edited-chip']")).ToBeVisibleAsync(new() { Timeout = 15_000 });

        // 6. The editor still says Edited on a fresh load; restore puts the original back and deletes it.
        await GotoAppAsync($"projects/{folder}/voices/{voiceId}/editor");
        await Expect(editor.Locator("[data-testid='edited-chip']")).ToBeVisibleAsync(new() { Timeout = 30_000 });
        await editor.Locator("[data-action='restore']").ClickAsync();
        await Page.Locator("r2m-confirm-dialog .r2m-confirm-dialog__confirm").ClickAsync();
        await Expect(editor.Locator("[data-testid='edited-chip']")).ToBeHiddenAsync(new() { Timeout = 30_000 });
        Assert.False(File.Exists(originalPath));
        Assert.Equal(beforeBytes, await File.ReadAllBytesAsync(livePath));
    }
}
