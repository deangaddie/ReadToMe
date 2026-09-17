using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Read2Me.E2eTests.Infrastructure;
using Read2Me.E2eTests.Infrastructure.FakeAi;

namespace Read2Me.E2eTests.Tests.Api;

/// <summary>
/// The per-voice endpoints Angular ticket 16 added: one voice by id, audio upload, transcription,
/// and the design-prompt render / generate pair.
/// </summary>
[Collection(E2eCollection.Name)]
public class VoiceDetailApiTests(E2eAppFixture app)
{
    private static readonly HttpClient Http = new();

    private async Task<Guid> CharacterIdAsync(string folder, string name)
    {
        var doc = JsonDocument.Parse(
            await Http.GetStringAsync($"{app.BaseUrl}/api/projects/{folder}/characters"));
        return doc.RootElement.EnumerateArray()
            .Single(c => c.GetProperty("name").GetString() == name)
            .GetProperty("id").GetGuid();
    }

    private async Task<Guid> CreateVoiceAsync(string folder, Guid characterId, string name, bool isGenerated)
    {
        var response = await Http.PostAsJsonAsync($"{app.BaseUrl}/api/projects/{folder}/commands",
            new { type = "CreateVoice", characterId, name, isGenerated });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("newEntityId").GetGuid();
    }

    private static MultipartFormDataContent WavForm(string fileName)
    {
        var content = new ByteArrayContent(FakeAiResponses.SilentWav());
        content.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        return new MultipartFormDataContent { { content, "file", fileName } };
    }

    [Fact]
    public async Task Get_voice_by_id_answers_the_voice_and_404_for_unknown()
    {
        var folder = $"api-voice-get-{Guid.NewGuid():N}";
        await app.SeedProjectAsync(folder, "Voice Get", "Author");
        var aliceId = await CharacterIdAsync(folder, "Alice");
        var voiceId = await CreateVoiceAsync(folder, aliceId, "Prompt Voice", isGenerated: true);

        var voice = JsonDocument.Parse(
            await Http.GetStringAsync($"{app.BaseUrl}/api/projects/{folder}/voices/{voiceId}")).RootElement;
        Assert.Equal(voiceId, voice.GetProperty("id").GetGuid());
        Assert.Equal(aliceId, voice.GetProperty("characterId").GetGuid());
        Assert.Equal("Prompt Voice", voice.GetProperty("name").GetString());
        Assert.Equal("Generated", voice.GetProperty("source").GetString());
        Assert.False(voice.GetProperty("isEdited").GetBoolean());
        Assert.Equal(JsonValueKind.Null, voice.GetProperty("ttsSettingsOverrideJson").ValueKind);

        var missing = await Http.GetAsync($"{app.BaseUrl}/api/projects/{folder}/voices/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task Upload_audio_then_transcribe_persists_both()
    {
        var folder = $"api-voice-upload-{Guid.NewGuid():N}";
        await app.SeedProjectAsync(folder, "Voice Upload", "Author");
        var aliceId = await CharacterIdAsync(folder, "Alice");
        var voiceId = await CreateVoiceAsync(folder, aliceId, "Reference Voice", isGenerated: false);

        var upload = await Http.PutAsync(
            $"{app.BaseUrl}/api/projects/{folder}/voices/{voiceId}/audio", WavForm("sample.wav"));
        Assert.Equal(HttpStatusCode.OK, upload.StatusCode);
        var uploaded = JsonDocument.Parse(await upload.Content.ReadAsStringAsync()).RootElement;
        var audioFileName = uploaded.GetProperty("audioFileName").GetString();
        Assert.False(string.IsNullOrEmpty(audioFileName));
        Assert.True(File.Exists(Path.Combine(app.WorkspaceDir, folder,
            audioFileName!.Replace('/', Path.DirectorySeparatorChar))));

        var transcribe = await Http.PostAsync(
            $"{app.BaseUrl}/api/projects/{folder}/voices/{voiceId}/transcribe", null);
        Assert.Equal(HttpStatusCode.OK, transcribe.StatusCode);
        // The fake whisper echoes the last text any test synthesised (or "transcript" when none
        // has), so the check is "non-empty and persisted", not a literal.
        var transcript = JsonDocument.Parse(await transcribe.Content.ReadAsStringAsync())
            .RootElement.GetProperty("transcript").GetString();
        Assert.False(string.IsNullOrWhiteSpace(transcript));

        var voice = JsonDocument.Parse(
            await Http.GetStringAsync($"{app.BaseUrl}/api/projects/{folder}/voices/{voiceId}")).RootElement;
        Assert.Equal(transcript, voice.GetProperty("transcript").GetString());
        Assert.Equal(audioFileName, voice.GetProperty("audioFileName").GetString());
    }

    [Fact]
    public async Task Upload_rejects_a_missing_file_and_an_unknown_format()
    {
        var folder = $"api-voice-badupload-{Guid.NewGuid():N}";
        await app.SeedProjectAsync(folder, "Voice Bad Upload", "Author");
        var aliceId = await CharacterIdAsync(folder, "Alice");
        var voiceId = await CreateVoiceAsync(folder, aliceId, "Reference Voice", isGenerated: false);

        var empty = await Http.PutAsync(
            $"{app.BaseUrl}/api/projects/{folder}/voices/{voiceId}/audio",
            new MultipartFormDataContent { { new StringContent("x"), "other" } });
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);

        var text = await Http.PutAsync(
            $"{app.BaseUrl}/api/projects/{folder}/voices/{voiceId}/audio", WavForm("notes.txt"));
        Assert.Equal(HttpStatusCode.BadRequest, text.StatusCode);

        var unknownVoice = await Http.PutAsync(
            $"{app.BaseUrl}/api/projects/{folder}/voices/{Guid.NewGuid()}/audio", WavForm("sample.wav"));
        Assert.Equal(HttpStatusCode.NotFound, unknownVoice.StatusCode);
    }

    [Fact]
    public async Task Transcribe_without_audio_is_422()
    {
        var folder = $"api-voice-notranscribe-{Guid.NewGuid():N}";
        await app.SeedProjectAsync(folder, "Voice No Audio", "Author");
        var aliceId = await CharacterIdAsync(folder, "Alice");
        var voiceId = await CreateVoiceAsync(folder, aliceId, "Silent", isGenerated: false);

        var response = await Http.PostAsync(
            $"{app.BaseUrl}/api/projects/{folder}/voices/{voiceId}/transcribe", null);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task Render_then_generate_design_prompt_round_trips_the_llm()
    {
        var folder = $"api-voice-prompt-{Guid.NewGuid():N}";
        await app.SeedProjectAsync(folder, "Prompt Book", "P. Author");
        var aliceId = await CharacterIdAsync(folder, "Alice");
        app.FakeAi.LlmReply = _ => "  A warm, unhurried alto with a faint Scottish lilt.  ";

        var render = await Http.PostAsync(
            $"{app.BaseUrl}/api/projects/{folder}/characters/{aliceId}/design-prompt/render", null);
        Assert.Equal(HttpStatusCode.OK, render.StatusCode);
        var rendered = JsonDocument.Parse(await render.Content.ReadAsStringAsync())
            .RootElement.GetProperty("prompt").GetString();
        Assert.Contains("Alice", rendered);
        Assert.Contains("Prompt Book", rendered);

        var generate = await Http.PostAsJsonAsync(
            $"{app.BaseUrl}/api/projects/{folder}/characters/{aliceId}/design-prompt/generate",
            new { prompt = rendered });
        Assert.Equal(HttpStatusCode.OK, generate.StatusCode);
        var designPrompt = JsonDocument.Parse(await generate.Content.ReadAsStringAsync())
            .RootElement.GetProperty("designPrompt").GetString();
        Assert.Equal("A warm, unhurried alto with a faint Scottish lilt.", designPrompt);

        // Nothing was persisted: the character still has no voices.
        var voices = JsonDocument.Parse(await Http.GetStringAsync(
            $"{app.BaseUrl}/api/projects/{folder}/characters/{aliceId}/voices")).RootElement;
        Assert.Equal(0, voices.GetProperty("voices").GetArrayLength());

        var blank = await Http.PostAsJsonAsync(
            $"{app.BaseUrl}/api/projects/{folder}/characters/{aliceId}/design-prompt/generate", new { prompt = " " });
        Assert.Equal(HttpStatusCode.BadRequest, blank.StatusCode);

        var unknown = await Http.PostAsync(
            $"{app.BaseUrl}/api/projects/{folder}/characters/{Guid.NewGuid()}/design-prompt/render", null);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }
}
