using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Read2Me.Data;
using Read2Me.E2eTests.Infrastructure;
using Read2Me.Services.Audio;
using Read2Me.Services.Events;

namespace Read2Me.E2eTests.Tests;

[Collection(E2eCollection.Name)]
public class AudioRoundTripTests(E2eAppFixture app, PlaywrightFixture pw) : E2eTestBase(app, pw)
{
    // Full audio round trip against the fakes: TTS captures the synthesised text,
    // fake-whisper echoes it back (valid because the audio queue is serial), so the
    // WER check passes and the item completes with audio on disk plus a clean review.
    [Fact]
    public async Task Queueing_narration_synthesises_transcribes_and_verifies()
    {
        const string sourceText = "It was a dark and stormy night.";
        var builder = await App.SeedProjectAsync("audio-book", "Audio Book", "A. Author");
        await App.SeedNarratorVoiceAsync("audio-book");
        var itemId = builder.ItemId("n1");

        await GotoAsync("/project/audio-book");
        await Page.GetByText("ch1").ClickAsync();
        await Page.GetByText("Split: Audio").ClickAsync();

        // The narrator's default voice resolves for the narration item.
        var voicePreview = Page.Locator($"[data-testid='voice-preview-{itemId}']");
        await Expect(voicePreview).ToContainTextAsync("Narrator Voice");

        // Select the narration item and queue it.
        var row = Page.Locator(".paragraph-item-hover-row",
            new() { Has = Page.Locator($"[data-testid='voice-preview-{itemId}']") });
        // Capture pipeline events to assert the transcription round trip.
        var events = new List<AudioGenEvent>();
        var broadcaster = App.Services.GetRequiredService<EventBroadcaster<AudioGenEvent>>();
        void Capture(AudioGenEvent e) { lock (events) events.Add(e); }
        broadcaster.Event += Capture;

        // Keyboard toggle: MudCheckBox is a controlled component, so Playwright's
        // CheckAsync fails its immediate state verification; Space fires the change
        // event and the Blazor round trip re-renders the checked state.
        var checkbox = row.Locator("input[type=checkbox]");
        await checkbox.FocusAsync();
        await Page.Keyboard.PressAsync(" ");
        await Expect(Page.GetByText("1 item selected")).ToBeVisibleAsync();
        await Page.GetByRole(Microsoft.Playwright.AriaRole.Button, new() { Name = "Add to Audio queue" })
            .ClickAsync();

        // Pipeline completes: the recorded take reconciles the Book View and the play button appears.
        await Expect(Page.Locator($"[data-testid='audio-play-{itemId}']"))
            .ToBeVisibleAsync(new() { Timeout = 30_000 });

        // Audio landed on disk.
        var folderPath = Path.Combine(App.WorkspaceDir, "audio-book");
        Assert.True(File.Exists(Path.Combine(folderPath, "audio", $"{itemId}.wav")));

        // Round trip recorded: audio path set, and no AudioReviews row —
        // row presence signals a failed stage, so absence means verify passed.
        var factory = App.Services.GetRequiredService<IProjectDbContextFactory>();
        await using var db = await factory.CreateAsync(folderPath);
        var item = await db.ParagraphItems.AsNoTracking().SingleAsync(pi => pi.Id == itemId);
        Assert.Equal($"audio/{itemId}.wav", item.AudioFileName);
        Assert.False(await db.AudioReviews.AsNoTracking().AnyAsync(r => r.ParagraphItemId == itemId));

        // Fake-whisper echoed the synthesised text back, so the transcript matches
        // the source exactly and the WER check passed without a semantic rescue.
        broadcaster.Event -= Capture;
        AudioGenEvent[] captured;
        lock (events) captured = [.. events];
        var transcribed = Assert.Single(captured.OfType<Transcribed>(), e => e.Id == itemId);
        Assert.Equal(sourceText, transcribed.Transcript);
        var verified = Assert.Single(captured.OfType<Verified>(), e => e.Id == itemId);
        Assert.True(verified.Ok);
        Assert.Equal(0, verified.Wer);
        Assert.False(verified.Rescued);
    }
}
