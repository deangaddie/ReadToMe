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
/// queue, confirm the wav landed and the item endpoint reflects completion.
/// </summary>
[Collection(E2eCollection.Name)]
public class AudioApiTests(E2eAppFixture app)
{
    private static readonly HttpClient Http = new();

    [Fact]
    public async Task Enqueue_poll_and_audio_lands_on_disk()
    {
        app.FakeAi.Reset();
        var folder = $"api-audio-{Guid.NewGuid():N}";
        var builder = await app.SeedProjectAsync(folder, "Audio Api Book", "Author");
        await app.SeedNarratorVoiceAsync(folder);
        var chapterId = builder.ChapterId("ch1");
        var itemId = builder.ItemId("n1");

        var enqueue = await Http.PostAsJsonAsync(
            $"{app.BaseUrl}/api/projects/{folder}/audio/enqueue",
            new { level = "chapter", nodeId = chapterId, needsAudioOnly = true });
        Assert.Equal(HttpStatusCode.Accepted, enqueue.StatusCode);
        var enqueued = JsonDocument.Parse(await enqueue.Content.ReadAsStringAsync())
            .RootElement.GetProperty("enqueued").GetInt32();
        Assert.Equal(2, enqueued); // n1 + n2 narration; unattributed character line excluded

        await app.WaitForQueueDrainAsync("/api/audio/queue", timeoutSeconds: 60);

        Assert.True(File.Exists(Path.Combine(app.WorkspaceDir, folder, "audio", $"{itemId}.wav")));

        var status = JsonDocument.Parse(await Http.GetStringAsync(
            $"{app.BaseUrl}/api/projects/{folder}/audio/items/{itemId}"));
        Assert.Equal(JsonValueKind.Null, status.RootElement.GetProperty("status").ValueKind);
        Assert.Equal(JsonValueKind.Null, status.RootElement.GetProperty("outcome").ValueKind);
        Assert.NotEqual(JsonValueKind.Null, status.RootElement.GetProperty("audioVersion").ValueKind);

        // The seeded VoxCPM2 config speaks on audio.cpp: a controllable clone, reference but no transcript.
        List<JsonObject> bodies;
        lock (app.FakeAi.AudioCppSpeechBodies) bodies = [.. app.FakeAi.AudioCppSpeechBodies];
        Assert.Contains(bodies, b => b["input"]!.GetValue<string>() == "It was a dark and stormy night.");
        Assert.All(bodies, b =>
        {
            Assert.Equal("voxcpm2", b["model"]!.GetValue<string>());
            Assert.Equal("base64", b["voice_ref"]!["type"]!.GetValue<string>());
            Assert.False(b.ContainsKey("reference_text"));
        });
    }

    /// <summary>
    /// The audio.cpp tracer: with the Breeze config active, one audio-queue run goes through the
    /// shared audio.cpp client (gate check, then speech request) and lands its audio.
    /// </summary>
    [Fact]
    public async Task A_Breeze_config_generates_through_audiocpp()
    {
        app.FakeAi.Reset();
        using var scope = app.Services.CreateScope();
        var tts = scope.ServiceProvider.GetRequiredService<ParagraphTtsSettingsService>();
        var previous = await tts.GetActiveConfigIdAsync();
        var breeze = (await tts.GetAllConfigsAsync()).Single(c => c.Name == WorkspaceSeeder.BreezeConfigName);
        await tts.SetActiveConfigAsync(breeze.Id);
        try
        {
            var folder = $"api-audio-breeze-{Guid.NewGuid():N}";
            var builder = await app.SeedProjectAsync(folder, "Breeze Book", "Author");
            await app.SeedNarratorVoiceAsync(folder);
            var itemId = builder.ItemId("n1");

            var enqueue = await Http.PostAsJsonAsync(
                $"{app.BaseUrl}/api/projects/{folder}/audio/enqueue",
                new { level = "chapter", nodeId = builder.ChapterId("ch1"), needsAudioOnly = true });
            Assert.Equal(HttpStatusCode.Accepted, enqueue.StatusCode);

            await app.WaitForQueueDrainAsync("/api/audio/queue", timeoutSeconds: 60);

            Assert.True(File.Exists(Path.Combine(app.WorkspaceDir, folder, "audio", $"{itemId}.wav")));
            List<JsonObject> bodies;
            lock (app.FakeAi.AudioCppSpeechBodies) bodies = [.. app.FakeAi.AudioCppSpeechBodies];
            Assert.Equal(2, bodies.Count); // n1 + n2
            Assert.All(bodies, b => Assert.Equal("breeze-q8", b["model"]!.GetValue<string>()));
            Assert.Contains(bodies, b => b["input"]!.GetValue<string>() == "It was a dark and stormy night.");
        }
        finally
        {
            if (previous is { } id) await tts.SetActiveConfigAsync(id);
        }
    }

    /// <summary>
    /// With the Chatterbox config active, an audio-queue run speaks on audio.cpp: a plain clone
    /// (reference, no transcript) with the language top-level and every knob sent explicitly under audio.cpp's names.
    /// </summary>
    [Fact]
    public async Task A_Chatterbox_config_generates_through_audiocpp()
    {
        app.FakeAi.Reset();
        using var scope = app.Services.CreateScope();
        var tts = scope.ServiceProvider.GetRequiredService<ParagraphTtsSettingsService>();
        var previous = await tts.GetActiveConfigIdAsync();
        var chatterbox = (await tts.GetAllConfigsAsync()).Single(c => c.Name == WorkspaceSeeder.ChatterboxConfigName);
        await tts.SetActiveConfigAsync(chatterbox.Id);
        try
        {
            var folder = $"api-audio-chatterbox-{Guid.NewGuid():N}";
            var builder = await app.SeedProjectAsync(folder, "Chatterbox Book", "Author");
            await app.SeedNarratorVoiceAsync(folder);
            var itemId = builder.ItemId("n1");

            var enqueue = await Http.PostAsJsonAsync(
                $"{app.BaseUrl}/api/projects/{folder}/audio/enqueue",
                new { level = "chapter", nodeId = builder.ChapterId("ch1"), needsAudioOnly = true });
            Assert.Equal(HttpStatusCode.Accepted, enqueue.StatusCode);

            await app.WaitForQueueDrainAsync("/api/audio/queue", timeoutSeconds: 60);

            Assert.True(File.Exists(Path.Combine(app.WorkspaceDir, folder, "audio", $"{itemId}.wav")));
            List<JsonObject> bodies;
            lock (app.FakeAi.AudioCppSpeechBodies) bodies = [.. app.FakeAi.AudioCppSpeechBodies];
            Assert.Equal(2, bodies.Count); // n1 + n2
            Assert.Contains(bodies, b => b["input"]!.GetValue<string>() == "It was a dark and stormy night.");
            Assert.All(bodies, b =>
            {
                Assert.Equal("chatterbox", b["model"]!.GetValue<string>());
                Assert.Equal("base64", b["voice_ref"]!["type"]!.GetValue<string>());
                Assert.False(b.ContainsKey("reference_text"));
                var options = b["options"]!.AsObject();
                Assert.Equal(
                    ["exaggeration", "guidance_scale", "temperature", "min_p", "top_p", "repetition_penalty", "seed"],
                    options.Select(o => o.Key));
                Assert.Equal("en", b["language"]!.GetValue<string>());
            });
        }
        finally
        {
            if (previous is { } id) await tts.SetActiveConfigAsync(id);
        }
    }

    /// <summary>
    /// With the Qwen3-Base config active, an audio-queue run speaks on audio.cpp: an ICL clone
    /// (reference plus the voice's transcript), the language top-level, and only a seed in options
    /// while the sampling knobs are unset.
    /// </summary>
    [Fact]
    public async Task A_Qwen3Base_config_generates_through_audiocpp()
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
                new { level = "chapter", nodeId = builder.ChapterId("ch1"), needsAudioOnly = true });
            Assert.Equal(HttpStatusCode.Accepted, enqueue.StatusCode);

            await app.WaitForQueueDrainAsync("/api/audio/queue", timeoutSeconds: 60);

            Assert.True(File.Exists(Path.Combine(app.WorkspaceDir, folder, "audio", $"{itemId}.wav")));
            List<JsonObject> bodies;
            lock (app.FakeAi.AudioCppSpeechBodies) bodies = [.. app.FakeAi.AudioCppSpeechBodies];
            Assert.Equal(2, bodies.Count); // n1 + n2
            Assert.Contains(bodies, b => b["input"]!.GetValue<string>() == "It was a dark and stormy night.");
            Assert.All(bodies, b =>
            {
                Assert.Equal("qwen3-base", b["model"]!.GetValue<string>());
                Assert.Equal("base64", b["voice_ref"]!["type"]!.GetValue<string>());
                Assert.Equal("The narrator's reference line.", b["reference_text"]!.GetValue<string>());
                Assert.Equal("auto", b["language"]!.GetValue<string>());
                Assert.Equal(["seed"], b["options"]!.AsObject().Select(o => o.Key));
            });
        }
        finally
        {
            if (previous is { } id) await tts.SetActiveConfigAsync(id);
        }
    }

    /// <summary>
    /// The verify half of the audio round trip: fake-whisper echoes the synthesised text back (valid
    /// because the audio queue is serial), so the take is transcribed, passes the WER check without
    /// a semantic rescue, and leaves no AudioReviews row — a row exists only for a failed stage.
    /// </summary>
    [Fact]
    public async Task A_take_is_transcribed_back_and_verified_without_a_review()
    {
        app.FakeAi.Reset();
        var folder = $"api-audio-verify-{Guid.NewGuid():N}";
        var builder = await app.SeedProjectAsync(folder, "Audio Verify Book", "Author");
        await app.SeedNarratorVoiceAsync(folder);
        var itemId = builder.ItemId("n1");

        var events = new List<AudioGenEvent>();
        var broadcaster = app.Services.GetRequiredService<EventBroadcaster<AudioGenEvent>>();
        void Capture(AudioGenEvent e) { lock (events) events.Add(e); }
        broadcaster.Event += Capture;
        try
        {
            var enqueue = await Http.PostAsJsonAsync(
                $"{app.BaseUrl}/api/projects/{folder}/audio/enqueue",
                new { level = "chapter", nodeId = builder.ChapterId("ch1"), needsAudioOnly = true });
            Assert.Equal(HttpStatusCode.Accepted, enqueue.StatusCode);
            await app.WaitForQueueDrainAsync("/api/audio/queue", timeoutSeconds: 60);
        }
        finally
        {
            broadcaster.Event -= Capture;
        }

        AudioGenEvent[] captured;
        lock (events) captured = [.. events];
        var transcribed = Assert.Single(captured.OfType<Transcribed>(), e => e.Id == itemId);
        Assert.Equal("It was a dark and stormy night.", transcribed.Transcript);
        var verified = Assert.Single(captured.OfType<Verified>(), e => e.Id == itemId);
        Assert.True(verified.Ok);
        Assert.Equal(0, verified.Wer);
        Assert.False(verified.Rescued);

        var factory = app.Services.GetRequiredService<IProjectDbContextFactory>();
        await using var db = await factory.CreateAsync(Path.Combine(app.WorkspaceDir, folder));
        var item = await db.ParagraphItems.AsNoTracking().SingleAsync(pi => pi.Id == itemId);
        Assert.Equal($"audio/{itemId}.wav", item.AudioFileName);
        Assert.False(await db.AudioReviews.AsNoTracking().AnyAsync(r => r.ParagraphItemId == itemId));
    }

    [Fact]
    public async Task Enqueue_unknown_folder_is_404()
    {
        var response = await Http.PostAsJsonAsync(
            $"{app.BaseUrl}/api/projects/nope-audio/audio/enqueue",
            new { level = "chapter", nodeId = Guid.NewGuid() });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Cancel_returns_ok()
    {
        var response = await Http.PostAsync($"{app.BaseUrl}/api/audio/cancel", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
