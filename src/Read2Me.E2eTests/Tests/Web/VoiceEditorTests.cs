using Microsoft.Playwright;
using Read2Me.E2eTests.Infrastructure;

namespace Read2Me.E2eTests.Tests.Web;

/// <summary>
/// The Angular voice audio editor (ticket 18) end to end: tick two steps, preview, hear both stages,
/// apply, see Edited on the cast card and in Blazor's editor, restore. Asserts on the <b>files</b> as
/// the Blazor test does — <c>{voiceId}.orig.wav</c> exists ⟺ the voice has been edited. The filters
/// are ffmpeg-gated and may skip on this host, so the live bytes are not asserted to differ.
/// </summary>
[Collection(E2eCollection.Name)]
public class VoiceEditorTests(E2eAppFixture app, PlaywrightFixture pw) : WebE2eTestBase(app, pw)
{
    private static readonly HttpClient Http = new();

    [Fact]
    public async Task Tick_preview_apply_restore_round_trip()
    {
        const string folder = "web-voice-editor";
        var book = await App.SeedProjectAsync(folder, "Voice Editor Book", "A. Author", characterName: "Alice");
        var alice = book.CharacterId("Alice");
        var voiceId = await App.SeedEditableVoiceAsync(folder, alice);

        var voicesDir = Path.Combine(App.WorkspaceDir, folder, "voices", alice.ToString());
        var livePath = Directory.GetFiles(voicesDir, $"{voiceId}-*.wav").Single();
        var originalPath = Path.Combine(voicesDir, $"{voiceId}.orig.wav");
        var beforeBytes = await File.ReadAllBytesAsync(livePath);

        // 1. The cast card's Edit audio link lands on the editor.
        await GotoAppAsync($"/app/projects/{folder}/cast/{alice}");
        var card = Page.Locator($"app-voice-card[data-voice-id='{voiceId}']");
        await card.Locator("mat-expansion-panel-header").First.ClickAsync();
        await card.Locator("[data-action='edit-audio']").ClickAsync();
        await Assertions.Expect(Page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex($"/app/projects/{folder}/voices/{voiceId}/editor$"));

        var editor = Page.Locator("app-voice-editor-page");
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

        // 5. Apply captures the original; the card and Blazor's editor both say Edited.
        await apply.ClickAsync();
        await Expect(editor.Locator("[data-testid='edited-chip']")).ToBeVisibleAsync(new() { Timeout = 30_000 });
        Assert.True(File.Exists(originalPath));
        Assert.Equal(beforeBytes, await File.ReadAllBytesAsync(originalPath));

        await editor.Locator("[data-action='back']").ClickAsync();
        await Expect(card.Locator("[data-testid='voice-edited-chip']")).ToBeVisibleAsync(new() { Timeout = 15_000 });

        await GotoAsync($"/project/{folder}/voice/{voiceId}/audio");
        await Expect(Page.Locator("[data-testid='edited-chip']")).ToBeVisibleAsync(new() { Timeout = 30_000 });

        // 6. Restore puts the original back and deletes it.
        await GotoAppAsync($"/app/projects/{folder}/voices/{voiceId}/editor");
        await editor.Locator("[data-action='restore']").ClickAsync();
        await Page.Locator("r2m-confirm-dialog .r2m-confirm-dialog__confirm").ClickAsync();
        await Expect(editor.Locator("[data-testid='edited-chip']")).ToBeHiddenAsync(new() { Timeout = 30_000 });
        Assert.False(File.Exists(originalPath));
        Assert.Equal(beforeBytes, await File.ReadAllBytesAsync(livePath));
    }
}
