using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using Read2Me.E2eTests.Infrastructure;
using Read2Me.Services;

namespace Read2Me.E2eTests.Tests;

/// <summary>
/// The voice audio editor end to end: edit → apply → restore. Asserts on the <b>files</b>, not just the
/// DOM — the invariant under test is <c>{voiceId}.orig.wav</c> exists ⟺ the voice has been edited.
/// <para>
/// ffmpeg-gated: silently no-ops when ffmpeg is absent, like every other filter test in the repo.
/// ffmpeg is not on PATH here (it lives at <c>D:\Dev\ffmpeg\bin</c>), so the probed path is written to
/// the seeded app settings — the steps read it from there.
/// </para>
/// </summary>
[Collection(E2eCollection.Name)]
public class VoiceAudioEditorTests(E2eAppFixture app, PlaywrightFixture pw) : E2eTestBase(app, pw)
{
    private static readonly string[] CandidateFfmpegPaths =
    [
        "ffmpeg",
        @"D:\Dev\ffmpeg\bin\ffmpeg.exe",
    ];

    private static string? FindFfmpeg()
    {
        foreach (var candidate in CandidateFfmpegPaths)
        {
            try
            {
                var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = candidate,
                    Arguments = "-version",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                });
                p?.WaitForExit(3000);
                if (p?.ExitCode == 0) return candidate;
            }
            catch { /* try the next candidate */ }
        }

        return null;
    }

    [Fact]
    public async Task Edit_apply_restore_round_trip()
    {
        var ffmpeg = FindFfmpeg();
        Assert.SkipWhen(ffmpeg is null, "ffmpeg not found — the editor's filters cannot run.");

        using (var scope = App.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<AudioProcessingSettingsService>()
                .SetFfmpegPathAsync(ffmpeg);

        var builder = await App.SeedProjectAsync("voice-edit", "Voice Edit", "A. Author");
        var characterId = builder.CharacterId("Alice");
        var voiceId = await App.SeedEditableVoiceAsync("voice-edit", characterId);

        var voicesDir = Path.Combine(App.WorkspaceDir, "voice-edit", "voices", characterId.ToString());
        var livePath = Directory.GetFiles(voicesDir, $"{voiceId}-*.wav").Single();
        var originalPath = Path.Combine(voicesDir, $"{voiceId}.orig.wav");
        var beforeBytes = await File.ReadAllBytesAsync(livePath);

        // 1. The characters tab offers the editor on a voice that has audio.
        await GotoAsync("/project/voice-edit");
        await Page.GetByRole(AriaRole.Tab, new() { Name = "Characters" }).ClickAsync();
        await Page.GetByText("Alice", new() { Exact = true }).First.ClickAsync();

        var editIcon = Page.Locator($"[data-testid='voice-edit-audio-{voiceId}']");
        await Expect(editIcon).ToBeVisibleAsync();
        await editIcon.ClickAsync();

        await Assertions.Expect(Page).ToHaveURLAsync(
            new System.Text.RegularExpressions.Regex($"/project/voice-edit/voice/{voiceId}/audio$"));

        // 2. Apply is blocked until a step is ticked, then until it has been previewed.
        var apply = Page.Locator("[data-testid='apply-button']");
        await Expect(apply).ToBeDisabledAsync();

        await Page.Locator("[data-testid='step-tick-silence-trim'] input[type=checkbox]").FocusAsync();
        await Page.Keyboard.PressAsync(" ");
        await Expect(apply).ToBeDisabledAsync();

        // 3. Preview stacks a player per ticked step under the original.
        await Page.Locator("[data-testid='preview-button']").ClickAsync();
        await Expect(Page.Locator("[data-testid='player-original']")).ToBeVisibleAsync(new() { Timeout = 30_000 });
        await Expect(Page.Locator("[data-testid='player-silence-trim']")).ToBeVisibleAsync(new() { Timeout = 30_000 });
        await Expect(apply).ToBeEnabledAsync();

        // 4. Apply captures the original and overwrites the live WAV in place.
        await apply.ClickAsync();
        await Expect(Page.Locator("[data-testid='edited-chip']")).ToBeVisibleAsync(new() { Timeout = 30_000 });

        Assert.True(File.Exists(originalPath));
        var afterBytes = await File.ReadAllBytesAsync(livePath);
        Assert.NotEqual(beforeBytes, afterBytes);
        // The original is the pre-edit audio, byte for byte.
        Assert.Equal(beforeBytes, await File.ReadAllBytesAsync(originalPath));

        // 5. Restore puts the original back and deletes it — which is what keeps the invariant exact.
        await Page.Locator("[data-testid='restore-original']").ClickAsync();
        await Page.Locator("[data-testid='confirm-ok']").ClickAsync();
        await Expect(Page.Locator("[data-testid='edited-chip']")).ToBeHiddenAsync(new() { Timeout = 30_000 });

        Assert.False(File.Exists(originalPath));
        Assert.Equal(beforeBytes, await File.ReadAllBytesAsync(livePath));
    }
}
