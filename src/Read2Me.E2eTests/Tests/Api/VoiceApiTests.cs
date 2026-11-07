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
public class VoiceApiTests(E2eAppFixture app) : IClassFixture<E2eAppFixture>
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

    [Fact]
    public async Task Batch_prompts_then_single_audio_generation()
    {
        var folder = $"api-voice-{Guid.NewGuid():N}";
        await app.SeedProjectAsync(folder, "Voice Book", "Author", characterName: "Alice");
        app.FakeAi.LlmReply = _ => VoicePlanReply;

        var start = await Http.PostAsJsonAsync(
            $"{app.BaseUrl}/api/projects/{folder}/voice-batch/prompts", new { regenerateAll = false });
        Assert.Equal(HttpStatusCode.Accepted, start.StatusCode);

        await app.WaitForVoiceBatchIdleAsync();

        var aliceId = await CharacterIdAsync(folder, "Alice");
        var voicesDoc = JsonDocument.Parse(await Http.GetStringAsync(
            $"{app.BaseUrl}/api/projects/{folder}/characters/{aliceId}/voices"));
        // What the plan writes is VoiceBatchRunnerIntegrationTests; here one voice has to land.
        var voice = voicesDoc.RootElement.GetProperty("voices")[0];
        Assert.Equal(JsonValueKind.String, voice.GetProperty("designPrompt").ValueKind);
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

        // The seeded VoxCPM2 config is the one that designed; the body it sends is its client tests.
        JsonObject body;
        lock (app.FakeAi.AudioCppSpeechBodies) body = app.FakeAi.AudioCppSpeechBodies[^1];
        Assert.Equal("voxcpm2", body["model"]!.GetValue<string>());
    }

    /// <summary>
    /// The wiring for a design provider other than the default: with the Qwen3 config active,
    /// generating a voice's audio goes to fake-audiocpp as a <c>qwen3-design</c> request. Every
    /// design provider's client and body are unit
    /// tests (ProviderClientRegistrationTests and each *VoiceDesignClientTests class), so one
    /// provider stands for the rest here.
    /// </summary>
    [Fact]
    public async Task An_active_design_provider_config_designs_through_its_own_audiocpp_client()
    {
        var body = await DesignOneVoiceAsync(WorkspaceSeeder.Qwen3DesignConfigName);

        Assert.Equal("qwen3-design", body["model"]!.GetValue<string>());
    }

    /// <summary>
    /// Generate audio carrying an unsaved design prompt: one request designs from that prompt and
    /// stores it with the take, so the edit and the audio land together. The endpoint picks the
    /// prompt, so no unit test pins it.
    /// </summary>
    [Fact]
    public async Task Generate_audio_with_a_design_prompt_designs_from_it_and_stores_it_with_the_take()
    {
        var (folder, aliceId, voiceId) = await PlanOneVoiceAsync();
        app.FakeAi.Reset();

        var gen = await Http.PostAsJsonAsync(
            $"{app.BaseUrl}/api/projects/{folder}/characters/{aliceId}/voices/{voiceId}/generate-audio",
            new { designPrompt = "A gravelly old voice." });

        Assert.Equal(HttpStatusCode.OK, gen.StatusCode);
        // The endpoint hands the sent prompt to the design, not the stored one.
        JsonObject body;
        lock (app.FakeAi.AudioCppSpeechBodies) body = Assert.Single(app.FakeAi.AudioCppSpeechBodies);
        Assert.StartsWith("(A gravelly old voice.)", body["input"]!.GetValue<string>());

        var voice = JsonDocument.Parse(await Http.GetStringAsync(
            $"{app.BaseUrl}/api/projects/{folder}/voices/{voiceId}")).RootElement;
        Assert.Equal("A gravelly old voice.", voice.GetProperty("designPrompt").GetString());
        Assert.False(string.IsNullOrEmpty(voice.GetProperty("audioFileName").GetString()));
    }

    [Fact]
    public async Task Generate_audio_with_a_malformed_body_is_a_400_and_designs_nothing()
    {
        var (folder, aliceId, voiceId) = await PlanOneVoiceAsync();
        app.FakeAi.Reset();

        var gen = await Http.PostAsync(
            $"{app.BaseUrl}/api/projects/{folder}/characters/{aliceId}/voices/{voiceId}/generate-audio",
            new StringContent("{ \"designPrompt\": ", System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, gen.StatusCode);
        lock (app.FakeAi.AudioCppSpeechBodies) Assert.Empty(app.FakeAi.AudioCppSpeechBodies);
    }

    /// <summary>
    /// Plans one voice for a fresh project's Alice, then generates its audio with the named seeded
    /// voice-design config active (restoring the previous one after). Asserts the take lands and
    /// returns the one audio.cpp speech body it sent.
    /// </summary>
    private async Task<JsonObject> DesignOneVoiceAsync(string configName)
    {
        var (folder, aliceId, voiceId) = await PlanOneVoiceAsync();

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

    /// <summary>A fresh project whose Alice has one planned (prompt-only) designed voice.</summary>
    private async Task<(string Folder, Guid AliceId, Guid VoiceId)> PlanOneVoiceAsync()
    {
        var folder = $"api-voice-design-{Guid.NewGuid():N}";
        await app.SeedProjectAsync(folder, "Designed Voice Book", "Author", characterName: "Alice");
        app.FakeAi.LlmReply = _ => VoicePlanReply;
        await Http.PostAsJsonAsync(
            $"{app.BaseUrl}/api/projects/{folder}/voice-batch/prompts", new { regenerateAll = false });
        await app.WaitForVoiceBatchIdleAsync();

        var aliceId = await CharacterIdAsync(folder, "Alice");
        var voiceId = JsonDocument.Parse(await Http.GetStringAsync(
                $"{app.BaseUrl}/api/projects/{folder}/characters/{aliceId}/voices"))
            .RootElement.GetProperty("voices")[0].GetProperty("id").GetGuid();
        return (folder, aliceId, voiceId);
    }

    /// <summary>
    /// The Reference Limit on a designed take, measured after the host normalises it: over 15 s it
    /// is kept and the answer carries <c>referenceWarning</c>; over 30 s generation is a 422 with a
    /// <c>detail</c>. The limits, their messages and the take kept on a refusal are
    /// ReferenceLimitTests and VoiceAudioGeneratorTests.
    /// </summary>
    [Fact]
    public async Task A_designed_take_over_the_Reference_Limit_is_warned_or_refused()
    {
        var (folder, aliceId, voiceId) = await PlanOneVoiceAsync();
        var generate = $"{app.BaseUrl}/api/projects/{folder}/characters/{aliceId}/voices/{voiceId}/generate-audio";
        try
        {
            app.FakeAi.AudioCppTakeMs = 16_000;
            var kept = await Http.PostAsync(generate, null);
            Assert.Equal(HttpStatusCode.OK, kept.StatusCode);
            var body = JsonDocument.Parse(await kept.Content.ReadAsStringAsync()).RootElement;
            Assert.False(string.IsNullOrEmpty(body.GetProperty("referenceWarning").GetString()));

            app.FakeAi.AudioCppTakeMs = 31_000;
            var refused = await Http.PostAsync(generate, null);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
            var problem = JsonDocument.Parse(await refused.Content.ReadAsStringAsync()).RootElement;
            Assert.False(string.IsNullOrEmpty(problem.GetProperty("detail").GetString()));
        }
        finally
        {
            app.FakeAi.AudioCppTakeMs = 100;
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
    public async Task Voice_batch_cancel_answers_no_content()
    {
        var response = await Http.PostAsync($"{app.BaseUrl}/api/voice-batch/cancel", null);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
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
