using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Read2Me.E2eTests.Infrastructure;

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

        // Single-voice audio generation against the fake voice-design + whisper services.
        var gen = await Http.PostAsync(
            $"{app.BaseUrl}/api/projects/{folder}/characters/{aliceId}/voices/{voiceId}/generate-audio", null);
        Assert.Equal(HttpStatusCode.OK, gen.StatusCode);
        var result = JsonDocument.Parse(await gen.Content.ReadAsStringAsync());
        var audioFileName = result.RootElement.GetProperty("audioFileName").GetString();
        Assert.False(string.IsNullOrEmpty(audioFileName));
        Assert.True(File.Exists(Path.Combine(app.WorkspaceDir, folder,
            audioFileName!.Replace('/', Path.DirectorySeparatorChar))));
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
