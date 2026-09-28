using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Read2Me.E2eTests.Infrastructure;
using Read2Me.Services;

namespace Read2Me.E2eTests.Tests.Api;

/// <summary>
/// Voices over HTTP: list, batch prompt generation with status polling, and
/// single-voice audio generation against the fake voice-design service.
/// </summary>
[Collection(E2eCollection.Name)]
public class VoiceApiTests(E2eAppFixture app)
{
    private static readonly HttpClient Http = new();

    private const string VoicePlanReply =
        """[ { "name": "Main Voice", "description": "the only voice", "design_prompt": "A clear adult voice." } ]""";

    private async Task<Guid> CharacterIdAsync(string folder, string name)
    {
        var doc = JsonDocument.Parse(
            await Http.GetStringAsync($"{app.BaseUrl}/api/projects/{folder}/characters"));
        return doc.RootElement.EnumerateArray()
            .Single(c => c.GetProperty("name").GetString() == name)
            .GetProperty("id").GetGuid();
    }

    private async Task WaitForBatchAsync()
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            var status = JsonDocument.Parse(
                await Http.GetStringAsync($"{app.BaseUrl}/api/voice-batch/status"));
            if (!status.RootElement.GetProperty("isRunning").GetBoolean())
                return;
            await Task.Delay(200);
        }
    }

    [Fact]
    public async Task Batch_prompts_then_single_audio_generation()
    {
        var folder = $"api-voice-{Guid.NewGuid():N}";
        await app.SeedProjectAsync(folder, "Voice Book", "Author", characterName: "Alice");
        app.FakeAi.LlmReply = _ => VoicePlanReply;

        var start = await Http.PostAsJsonAsync(
            $"{app.BaseUrl}/api/projects/{folder}/voice-batch/prompts", new { regenerateAll = false });
        Assert.Equal(HttpStatusCode.Accepted, start.StatusCode);

        await WaitForBatchAsync();

        var aliceId = await CharacterIdAsync(folder, "Alice");
        var voicesDoc = JsonDocument.Parse(await Http.GetStringAsync(
            $"{app.BaseUrl}/api/projects/{folder}/characters/{aliceId}/voices"));
        var voices = voicesDoc.RootElement.GetProperty("voices");
        Assert.Equal(1, voices.GetArrayLength());
        var voice = voices[0];
        Assert.Equal("Main Voice", voice.GetProperty("name").GetString());
        Assert.Equal("A clear adult voice.", voice.GetProperty("designPrompt").GetString());
        var voiceId = voice.GetProperty("id").GetGuid();

        // Single-voice audio generation against fake-audiocpp + fake-whisper.
        var gen = await Http.PostAsync(
            $"{app.BaseUrl}/api/projects/{folder}/characters/{aliceId}/voices/{voiceId}/generate-audio", null);
        Assert.Equal(HttpStatusCode.OK, gen.StatusCode);
        var result = JsonDocument.Parse(await gen.Content.ReadAsStringAsync());
        var audioFileName = result.RootElement.GetProperty("audioFileName").GetString();
        Assert.False(string.IsNullOrEmpty(audioFileName));
        Assert.True(File.Exists(Path.Combine(app.WorkspaceDir, folder,
            audioFileName!.Replace('/', Path.DirectorySeparatorChar))));

        // The seeded VoxCPM2 config designs on audio.cpp: no reference, the prompt as the (control) prefix.
        JsonObject body;
        lock (app.FakeAi.AudioCppSpeechBodies) body = app.FakeAi.AudioCppSpeechBodies[^1];
        Assert.Equal("voxcpm2", body["model"]!.GetValue<string>());
        Assert.StartsWith("(A clear adult voice.)", body["input"]!.GetValue<string>());
        Assert.False(body.ContainsKey("voice_ref"));
    }

    /// <summary>
    /// Breeze voice design: with the Breeze config active, generating a voice's audio sends its
    /// design prompt to fake-audiocpp as the instruction on a no-reference <c>breeze-design</c>
    /// request, and the take lands.
    /// </summary>
    [Fact]
    public async Task A_Breeze_voice_design_config_designs_through_audiocpp()
    {
        var body = await DesignOneVoiceAsync(WorkspaceSeeder.BreezeDesignConfigName);

        Assert.Equal("breeze-design", body["model"]!.GetValue<string>());
        Assert.Equal("A clear adult voice.", body["options"]!["instruction"]!.GetValue<string>());
        Assert.False(body.ContainsKey("voice_ref"));
    }

    /// <summary>
    /// Qwen3 voice design: with the Qwen3 config active, generating a voice's audio sends its design
    /// prompt to fake-audiocpp as the instruction on a no-reference <c>qwen3-design</c> request, the
    /// language top-level, and only the instruction and a seed in options while the knobs are unset.
    /// </summary>
    [Fact]
    public async Task A_Qwen3_voice_design_config_designs_through_audiocpp()
    {
        var body = await DesignOneVoiceAsync(WorkspaceSeeder.Qwen3DesignConfigName);

        Assert.Equal("qwen3-design", body["model"]!.GetValue<string>());
        Assert.Equal("A clear adult voice.", body["options"]!["instruction"]!.GetValue<string>());
        Assert.Equal(["instruction", "seed"], body["options"]!.AsObject().Select(o => o.Key).Order());
        Assert.Equal("auto", body["language"]!.GetValue<string>());
        Assert.False(body.ContainsKey("voice_ref"));
        Assert.False(body.ContainsKey("reference_text"));
    }

    /// <summary>
    /// Plans one voice for a fresh project's Alice, then generates its audio with the named seeded
    /// voice-design config active (restoring the previous one after). Asserts the take lands and
    /// returns the one audio.cpp speech body it sent.
    /// </summary>
    private async Task<JsonObject> DesignOneVoiceAsync(string configName)
    {
        var folder = $"api-voice-design-{Guid.NewGuid():N}";
        await app.SeedProjectAsync(folder, "Designed Voice Book", "Author", characterName: "Alice");
        app.FakeAi.LlmReply = _ => VoicePlanReply;
        await Http.PostAsJsonAsync(
            $"{app.BaseUrl}/api/projects/{folder}/voice-batch/prompts", new { regenerateAll = false });
        await WaitForBatchAsync();

        var aliceId = await CharacterIdAsync(folder, "Alice");
        var voiceId = JsonDocument.Parse(await Http.GetStringAsync(
                $"{app.BaseUrl}/api/projects/{folder}/characters/{aliceId}/voices"))
            .RootElement.GetProperty("voices")[0].GetProperty("id").GetGuid();

        app.FakeAi.Reset();
        using var scope = app.Services.CreateScope();
        var design = scope.ServiceProvider.GetRequiredService<VoiceDesignSettingsService>();
        var previous = await design.GetActiveConfigIdAsync();
        var config = (await design.GetAllConfigsAsync()).Single(c => c.Name == configName);
        await design.SetActiveConfigAsync(config.Id);
        try
        {
            var gen = await Http.PostAsync(
                $"{app.BaseUrl}/api/projects/{folder}/characters/{aliceId}/voices/{voiceId}/generate-audio", null);

            Assert.Equal(HttpStatusCode.OK, gen.StatusCode);
            var audioFileName = JsonDocument.Parse(await gen.Content.ReadAsStringAsync())
                .RootElement.GetProperty("audioFileName").GetString();
            Assert.True(File.Exists(Path.Combine(app.WorkspaceDir, folder,
                audioFileName!.Replace('/', Path.DirectorySeparatorChar))));

            lock (app.FakeAi.AudioCppSpeechBodies) return Assert.Single(app.FakeAi.AudioCppSpeechBodies);
        }
        finally
        {
            if (previous is { } id) await design.SetActiveConfigAsync(id);
        }
    }

    [Fact]
    public async Task Voice_batch_status_reports_idle_shape()
    {
        var status = JsonDocument.Parse(
            await Http.GetStringAsync($"{app.BaseUrl}/api/voice-batch/status"));

        Assert.True(status.RootElement.TryGetProperty("isRunning", out _));
        Assert.True(status.RootElement.TryGetProperty("processed", out _));
        Assert.True(status.RootElement.TryGetProperty("failed", out _));
    }

    [Fact]
    public async Task Voice_batch_cancel_returns_ok()
    {
        var response = await Http.PostAsync($"{app.BaseUrl}/api/voice-batch/cancel", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Generate_audio_for_unknown_voice_is_404()
    {
        var folder = $"api-voice2-{Guid.NewGuid():N}";
        await app.SeedProjectAsync(folder, "Voice Book 2", "Author");
        var aliceId = await CharacterIdAsync(folder, "Alice");

        var response = await Http.PostAsync(
            $"{app.BaseUrl}/api/projects/{folder}/characters/{aliceId}/voices/{Guid.NewGuid()}/generate-audio", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
