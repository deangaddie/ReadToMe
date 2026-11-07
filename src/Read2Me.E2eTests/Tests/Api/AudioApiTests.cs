using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Read2Me.Data;
using Read2Me.Services;
using Read2Me.Services.Audio;
using Read2Me.Services.Events;
using Read2Me.E2eTests.Infrastructure;

namespace Read2Me.E2eTests.Tests.Api;

/// <summary>
/// Audio generation over HTTP: enqueue narration items for a chapter, poll the
/// queue, confirm the wav landed, was verified and the item endpoint reflects completion.
/// </summary>
public class AudioApiTests(E2eAppFixture app) : IClassFixture<E2eAppFixture>
{
    private static readonly HttpClient Http = new();

    /// <summary>
    /// The audio round trip: enqueue a chapter, the take lands on disk and is served from the
    /// workspace mount, and fake-whisper echoes the synthesised text back (valid because the audio
    /// queue is serial), so the take is transcribed and verified and leaves no AudioReviews row. The
    /// request body each provider sends is its client tests; which items a chapter enqueue takes is
    /// ProjectReaderTests (GetAudioItemRefsAsync_…).
    /// </summary>
    [Fact]
    public async Task Enqueue_poll_and_audio_lands_is_verified_and_is_served_from_the_workspace()
    {
        app.FakeAi.Reset();
        var folder = $"api-audio-{Guid.NewGuid():N}";
        var builder = await app.SeedProjectAsync(folder, "Audio Api Book", "Author");
        await app.SeedNarratorVoiceAsync(folder);
        var chapterId = builder.ChapterId("ch1");
        var itemId = builder.ItemId("n1");

        var events = new List<AudioGenEvent>();
        var broadcaster = app.Services.GetRequiredService<EventBroadcaster<AudioGenEvent>>();
        void Capture(AudioGenEvent e) { lock (events) events.Add(e); }
        broadcaster.Event += Capture;
        try
        {
            var enqueue = await Http.PostAsJsonAsync(
                $"{app.BaseUrl}/api/projects/{folder}/audio/enqueue",
                new { level = "Chapter", nodeId = chapterId, needsAudioOnly = true });
            Assert.Equal(HttpStatusCode.Accepted, enqueue.StatusCode);
            Assert.Equal(JsonValueKind.Number, JsonDocument.Parse(await enqueue.Content.ReadAsStringAsync())
                .RootElement.GetProperty("enqueued").ValueKind);

            await app.WaitForQueueDrainAsync("/api/audio/queue", timeoutSeconds: 60);
        }
        finally
        {
            broadcaster.Event -= Capture;
        }

        Assert.True(File.Exists(Path.Combine(app.WorkspaceDir, folder, "audio", $"{itemId}.wav")));

        var status = JsonDocument.Parse(await Http.GetStringAsync(
            $"{app.BaseUrl}/api/projects/{folder}/audio/items/{itemId}"));
        Assert.Equal(JsonValueKind.Null, status.RootElement.GetProperty("status").ValueKind);
        Assert.Equal(JsonValueKind.Null, status.RootElement.GetProperty("outcome").ValueKind);
        var audioVersion = status.RootElement.GetProperty("audioVersion");
        Assert.NotEqual(JsonValueKind.Null, audioVersion.ValueKind);

        // The reader's player addresses the take through the workspace mount, by the file name the
        // chapter children carry and cache-busted by its version (workspace-url.ts).
        var children = JsonDocument.Parse(await Http.GetStringAsync(
            $"{app.BaseUrl}/api/projects/{folder}/nodes/Chapter/{chapterId}/children"));
        var audioFileName = children.RootElement.GetProperty("paragraphs").EnumerateArray()
            .SelectMany(p => p.GetProperty("items").EnumerateArray())
            .Single(i => i.GetProperty("id").GetGuid() == itemId)
            .GetProperty("audioFileName").GetString()!;
        var take = await Http.GetAsync(
            $"{app.BaseUrl}/workspace/{folder}/{audioFileName}?v={Uri.EscapeDataString(audioVersion.ToString())}");
        Assert.Equal(HttpStatusCode.OK, take.StatusCode);
        Assert.StartsWith("audio/", take.Content.Headers.ContentType?.MediaType);
        Assert.Equal(
            await File.ReadAllBytesAsync(Path.Combine(app.WorkspaceDir, folder, "audio", $"{itemId}.wav")),
            await take.Content.ReadAsByteArrayAsync());

        // The seeded VoxCPM2 config is the one that spoke.
        List<JsonObject> bodies;
        lock (app.FakeAi.AudioCppSpeechBodies) bodies = [.. app.FakeAi.AudioCppSpeechBodies];
        Assert.NotEmpty(bodies);
        Assert.All(bodies, b => Assert.Equal("voxcpm2", b["model"]!.GetValue<string>()));

        AudioGenEvent[] captured;
        lock (events) captured = [.. events];
        Assert.Single(captured.OfType<Transcribed>(), e => e.Id == itemId);
        Assert.True(Assert.Single(captured.OfType<Verified>(), e => e.Id == itemId).Ok);

        var factory = app.Services.GetRequiredService<IProjectDbContextFactory>();
        await using var db = await factory.CreateAsync(Path.Combine(app.WorkspaceDir, folder));
        Assert.False(await db.AudioReviews.AsNoTracking().AnyAsync(r => r.ParagraphItemId == itemId));
    }

    /// <summary>
    /// The wiring for a provider other than the default: with the Qwen3-Base config active, an
    /// audio-queue run goes through that provider's client to audio.cpp. Every provider's client and
    /// the body it sends are unit tests (ProviderClientRegistrationTests and each
    /// *ParagraphTtsClientTests class), so one provider stands for the rest here.
    /// </summary>
    [Fact]
    public async Task An_active_provider_config_generates_through_its_own_audiocpp_client()
    {
        app.FakeAi.Reset();
        using var scope = app.Services.CreateScope();
        var tts = scope.ServiceProvider.GetRequiredService<ParagraphTtsSettingsService>();
        var previous = await tts.GetActiveConfigIdAsync();
        var qwen3 = (await tts.GetAllConfigsAsync()).Single(c => c.Name == WorkspaceSeeder.Qwen3BaseConfigName);
        await tts.SetActiveConfigAsync(qwen3.Id);
        try
        {
            var folder = $"api-audio-qwen3-{Guid.NewGuid():N}";
            var builder = await app.SeedProjectAsync(folder, "Qwen3 Book", "Author");
            await app.SeedNarratorVoiceAsync(folder, transcript: "The narrator's reference line.");
            var itemId = builder.ItemId("n1");

            var enqueue = await Http.PostAsJsonAsync(
                $"{app.BaseUrl}/api/projects/{folder}/audio/enqueue",
                new { level = "Chapter", nodeId = builder.ChapterId("ch1"), needsAudioOnly = true });
            Assert.Equal(HttpStatusCode.Accepted, enqueue.StatusCode);

            await app.WaitForQueueDrainAsync("/api/audio/queue", timeoutSeconds: 60);

            Assert.True(File.Exists(Path.Combine(app.WorkspaceDir, folder, "audio", $"{itemId}.wav")));
            List<JsonObject> bodies;
            lock (app.FakeAi.AudioCppSpeechBodies) bodies = [.. app.FakeAi.AudioCppSpeechBodies];
            Assert.NotEmpty(bodies);
            Assert.All(bodies, b => Assert.Equal("qwen3-base", b["model"]!.GetValue<string>()));
        }
        finally
        {
            if (previous is { } id) await tts.SetActiveConfigAsync(id);
        }
    }

    [Fact]
    public async Task Enqueue_unknown_folder_is_404()
    {
        var response = await Http.PostAsJsonAsync(
            $"{app.BaseUrl}/api/projects/nope-audio/audio/enqueue",
            new { level = "Chapter", nodeId = Guid.NewGuid() });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Cancel_returns_no_content()
    {
        var response = await Http.PostAsync($"{app.BaseUrl}/api/audio/cancel", null);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }
}
