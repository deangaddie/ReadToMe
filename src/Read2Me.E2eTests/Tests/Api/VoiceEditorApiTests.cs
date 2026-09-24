using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Read2Me.E2eTests.Infrastructure;

namespace Read2Me.E2eTests.Tests.Api;

/// <summary>
/// The voice audio editor on the wire (Angular ticket 18): the step catalog, preview → per-stage
/// WAVs → apply-by-previewId, restore, and the stored original. Asserts on the <b>files</b> as well as
/// the DTOs — the invariant is <c>{voiceId}.orig.wav</c> exists ⟺ the voice has been edited.
/// Filters are ffmpeg-gated and may skip in this fixture; a skipped stage still answers its input, so
/// the round trip holds either way and <c>applied</c> is deliberately not asserted.
/// </summary>
[Collection(E2eCollection.Name)]
public class VoiceEditorApiTests(E2eAppFixture app)
{
    private static readonly HttpClient Http = new();

    private static readonly string[] VoiceStepOrder =
        ["de-plosive", "denoise", "hiss-reduce", "consonant-soften", "silence-trim"];

    private async Task<(string Folder, Guid CharacterId, Guid VoiceId)> SeedAsync(string prefix)
    {
        var folder = $"{prefix}-{Guid.NewGuid():N}";
        var builder = await app.SeedProjectAsync(folder, "Voice Editor", "Author");
        var characterId = builder.CharacterId("Alice");
        var voiceId = await app.SeedEditableVoiceAsync(folder, characterId);
        return (folder, characterId, voiceId);
    }

    private string EditorUrl(string folder, Guid voiceId) =>
        $"{app.BaseUrl}/api/projects/{folder}/voices/{voiceId}/editor";

    private async Task<JsonElement> PreviewAsync(string folder, Guid voiceId, object body)
    {
        var response = await Http.PostAsJsonAsync($"{EditorUrl(folder, voiceId)}/preview", body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
    }

    private static object TwoSteps() => new
    {
        steps = new object[]
        {
            new { stepId = "silence-trim", settings = new { thresholdDb = -40, padMs = 100 } },
            new { stepId = "denoise", settings = new { strength = 30 } },
        },
    };

    private async Task<string> LiveWavPathAsync(string folder, Guid voiceId)
    {
        var voice = JsonDocument.Parse(
            await Http.GetStringAsync($"{app.BaseUrl}/api/projects/{folder}/voices/{voiceId}")).RootElement;
        var relative = voice.GetProperty("audioFileName").GetString()!;
        return Path.Combine(app.WorkspaceDir, folder, relative.Replace('/', Path.DirectorySeparatorChar));
    }

    [Fact]
    public async Task Catalog_lists_the_voice_steps_in_chain_order_with_dials_and_defaults()
    {
        var catalog = JsonDocument.Parse(
            await Http.GetStringAsync($"{app.BaseUrl}/api/audio/steps/catalog?scope=voice")).RootElement;

        Assert.Equal(VoiceStepOrder, catalog.EnumerateArray().Select(s => s.GetProperty("stepId").GetString()));

        var trim = catalog.EnumerateArray().Single(s => s.GetProperty("stepId").GetString() == "silence-trim");
        Assert.Equal("Silence trim", trim.GetProperty("label").GetString());
        Assert.Equal("Trims dead air from the start and end.", trim.GetProperty("blurb").GetString());
        var threshold = trim.GetProperty("dials").EnumerateArray()
            .Single(d => d.GetProperty("key").GetString() == "thresholdDb");
        Assert.Equal("number", threshold.GetProperty("kind").GetString());
        Assert.Equal(-60, threshold.GetProperty("min").GetDouble());
        Assert.Equal(-35, threshold.GetProperty("max").GetDouble());
        Assert.Equal(-35, trim.GetProperty("defaults").GetProperty("thresholdDb").GetDouble());
        Assert.Equal(50, trim.GetProperty("defaults").GetProperty("padMs").GetInt32());

        var hiss = catalog.EnumerateArray().Single(s => s.GetProperty("stepId").GetString() == "hiss-reduce");
        var preset = hiss.GetProperty("dials").EnumerateArray().Single();
        Assert.Equal("enum", preset.GetProperty("kind").GetString());
        Assert.Equal(["light", "strong"],
            preset.GetProperty("options").EnumerateArray().Select(o => o.GetProperty("value").GetString()));
        Assert.Equal("light", hiss.GetProperty("defaults").GetProperty("preset").GetString());

        var other = await Http.GetAsync($"{app.BaseUrl}/api/audio/steps/catalog?scope=paragraph");
        Assert.Equal(HttpStatusCode.BadRequest, other.StatusCode);
    }

    [Fact]
    public async Task Preview_apply_restore_round_trip_moves_the_original_and_back()
    {
        var (folder, characterId, voiceId) = await SeedAsync("api-voice-editor");
        var livePath = await LiveWavPathAsync(folder, voiceId);
        var seeded = await File.ReadAllBytesAsync(livePath);

        var before = await Http.GetAsync($"{app.BaseUrl}/api/projects/{folder}/voices/{voiceId}/original.wav");
        Assert.Equal(HttpStatusCode.NotFound, before.StatusCode);

        // Steps are sent out of order; the host answers them in chain order.
        var preview = await PreviewAsync(folder, voiceId, TwoSteps());
        var previewId = preview.GetProperty("previewId").GetString()!;
        var stages = preview.GetProperty("stages").EnumerateArray().ToList();
        Assert.Equal(["denoise", "silence-trim"], stages.Select(s => s.GetProperty("stepId").GetString()));

        byte[]? final = null;
        foreach (var stage in stages)
        {
            var url = stage.GetProperty("url").GetString()!;
            Assert.Equal($"/api/previews/{previewId}/{stage.GetProperty("stepId").GetString()}.wav", url);
            var wav = await Http.GetAsync($"{app.BaseUrl}{url}");
            Assert.Equal(HttpStatusCode.OK, wav.StatusCode);
            Assert.Equal("audio/wav", wav.Content.Headers.ContentType!.MediaType);
            Assert.True(wav.Headers.CacheControl!.NoStore);
            final = await wav.Content.ReadAsByteArrayAsync();
            Assert.Equal(final.Length, wav.Content.Headers.ContentLength);
        }

        var applied = await Http.PostAsJsonAsync($"{EditorUrl(folder, voiceId)}/apply", new { previewId });
        Assert.Equal(HttpStatusCode.OK, applied.StatusCode);
        var voice = JsonDocument.Parse(await applied.Content.ReadAsStringAsync()).RootElement;
        Assert.True(voice.GetProperty("isEdited").GetBoolean());
        Assert.Equal(final, await File.ReadAllBytesAsync(livePath));

        var original = await Http.GetAsync($"{app.BaseUrl}/api/projects/{folder}/voices/{voiceId}/original.wav");
        Assert.Equal(HttpStatusCode.OK, original.StatusCode);
        Assert.Equal("audio/wav", original.Content.Headers.ContentType!.MediaType);
        Assert.Equal(seeded, await original.Content.ReadAsByteArrayAsync());
        Assert.True(File.Exists(Path.Combine(app.WorkspaceDir, folder, "voices", characterId.ToString(), $"{voiceId}.orig.wav")));

        var restored = await Http.PostAsync($"{EditorUrl(folder, voiceId)}/restore", null);
        Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
        voice = JsonDocument.Parse(await restored.Content.ReadAsStringAsync()).RootElement;
        Assert.False(voice.GetProperty("isEdited").GetBoolean());
        Assert.Equal(seeded, await File.ReadAllBytesAsync(livePath));
        var gone = await Http.GetAsync($"{app.BaseUrl}/api/projects/{folder}/voices/{voiceId}/original.wav");
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
    }

    [Fact]
    public async Task Preview_refuses_bad_chains_and_unknown_voices()
    {
        var (folder, _, voiceId) = await SeedAsync("api-voice-editor-bad");

        var empty = await Http.PostAsJsonAsync($"{EditorUrl(folder, voiceId)}/preview", new { steps = Array.Empty<object>() });
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);

        var unknownStep = await Http.PostAsJsonAsync($"{EditorUrl(folder, voiceId)}/preview",
            new { steps = new[] { new { stepId = "reverb", settings = new { } } } });
        Assert.Equal(HttpStatusCode.BadRequest, unknownStep.StatusCode);

        // The catalog's ranges are enforced, not just advertised: -10 dB would eat speech.
        var outOfRange = await Http.PostAsJsonAsync($"{EditorUrl(folder, voiceId)}/preview",
            new { steps = new[] { new { stepId = "silence-trim", settings = new { thresholdDb = -10 } } } });
        Assert.Equal(HttpStatusCode.BadRequest, outOfRange.StatusCode);
        var badPreset = await Http.PostAsJsonAsync($"{EditorUrl(folder, voiceId)}/preview",
            new { steps = new[] { new { stepId = "hiss-reduce", settings = new { preset = "extreme" } } } });
        Assert.Equal(HttpStatusCode.BadRequest, badPreset.StatusCode);

        var missingVoice = await Http.PostAsJsonAsync($"{EditorUrl(folder, Guid.NewGuid())}/preview", TwoSteps());
        Assert.Equal(HttpStatusCode.NotFound, missingVoice.StatusCode);
    }

    [Fact]
    public async Task Apply_refuses_previews_it_did_not_render_for_this_voice()
    {
        var (folder, _, voiceId) = await SeedAsync("api-voice-editor-apply");
        var (otherFolder, _, otherVoiceId) = await SeedAsync("api-voice-editor-apply-other");

        var unknown = await Http.PostAsJsonAsync($"{EditorUrl(folder, voiceId)}/apply", new { previewId = "nope" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, unknown.StatusCode);

        var otherPreview = await PreviewAsync(otherFolder, otherVoiceId, TwoSteps());
        var crossed = await Http.PostAsJsonAsync($"{EditorUrl(folder, voiceId)}/apply",
            new { previewId = otherPreview.GetProperty("previewId").GetString() });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, crossed.StatusCode);
        Assert.Empty(Directory.EnumerateFiles(
            Path.Combine(app.WorkspaceDir, folder, "voices"), "*.orig.wav", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Preview_urls_and_apply_stop_working_after_expiry()
    {
        var (folder, _, voiceId) = await SeedAsync("api-voice-editor-expiry");
        var preview = await PreviewAsync(folder, voiceId, TwoSteps());
        var previewId = preview.GetProperty("previewId").GetString()!;
        var url = preview.GetProperty("stages")[0].GetProperty("url").GetString()!;

        Assert.Equal(HttpStatusCode.OK, (await Http.GetAsync($"{app.BaseUrl}{url}")).StatusCode);

        app.Clock.Advance(TimeSpan.FromMinutes(31));

        Assert.Equal(HttpStatusCode.NotFound, (await Http.GetAsync($"{app.BaseUrl}{url}")).StatusCode);
        var apply = await Http.PostAsJsonAsync($"{EditorUrl(folder, voiceId)}/apply", new { previewId });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, apply.StatusCode);
    }
}
