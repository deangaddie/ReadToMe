using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Read2Me.App.Shared;
using Read2Me.AppData.Entities;
using Read2Me.E2eTests.Infrastructure;

namespace Read2Me.E2eTests.Tests.Api;

/// <summary>
/// The provider settings pages' endpoints beyond the generic config areas (Angular ticket 22):
/// the text-processing step catalog, the voice-design sample text, the three Test actions, the
/// transcription / similarity schemas and the canonical <c>settingsJson</c> every write stores.
/// </summary>
[Collection(E2eCollection.Name)]
public class ProviderSettingsApiTests(E2eAppFixture app)
{
    private static readonly HttpClient Http = new();

    private string Url(string rest) => $"{app.BaseUrl}/api/settings/{rest}";

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    private async Task<JsonElement> GetJsonAsync(string rest) =>
        JsonDocument.Parse(await Http.GetStringAsync(Url(rest))).RootElement;

    private async Task<int> FakeConfigIdAsync(string area) =>
        (await GetJsonAsync(area)).EnumerateArray()
            .First(c => c.GetProperty("name").GetString() == "fake").GetProperty("id").GetInt32();

    private async Task<JsonElement> CreateAsync(string area, object config)
    {
        var create = await Http.PostAsJsonAsync(Url(area), config);
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        return await JsonAsync(create);
    }

    // ── schemas ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("transcription", "LocalWhisper", null)]
    [InlineData("semantic-similarity", "MiniLmL6", "PassThreshold")]
    [InlineData("semantic-similarity", "MpnetBaseV2", "PassThreshold")]
    public async Task Schema_describes_the_transcription_and_similarity_types(string area, string type, string? expectedKey)
    {
        var schema = await GetJsonAsync($"{area}/schema?type={type}");

        Assert.Equal(type, schema.GetProperty("type").GetString());
        var keys = schema.GetProperty("fields").EnumerateArray().Select(f => f.GetProperty("key").GetString()).ToList();
        if (expectedKey is null) Assert.Empty(keys);
        else Assert.Equal([expectedKey], keys);

        var unknown = await Http.GetAsync(Url($"{area}/schema?type=Nope"));
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
    }

    // ── canonical settingsJson ───────────────────────────────────────────────

    [Fact]
    public async Task A_write_stores_settingsJson_exactly_as_the_Blazor_form_serialises_it()
    {
        // Wrong case, wrong order, a stray key: what any client may send.
        var created = await CreateAsync("semantic-similarity", new
        {
            name = $"canon-{Guid.NewGuid():N}",
            type = 0,
            settingsJson = """{ "passThreshold": 0.5, "baseUrl": "http://example-sim", "junk": 1 }""",
        });
        var id = created.GetProperty("id").GetInt32();
        try
        {
            Assert.Equal("""{"BaseUrl":"http://example-sim","PassThreshold":0.5}""",
                created.GetProperty("settingsJson").GetString());

            var update = await Http.PutAsJsonAsync(Url($"semantic-similarity/{id}"), new
            {
                id,
                name = created.GetProperty("name").GetString(),
                type = 0,
                settingsJson = """{"PassThreshold":0.9,"BaseUrl":"http://example-sim"}""",
            });
            Assert.Equal(HttpStatusCode.OK, update.StatusCode);
            var stored = (await GetJsonAsync("semantic-similarity")).EnumerateArray()
                .First(c => c.GetProperty("id").GetInt32() == id);
            Assert.Equal("""{"BaseUrl":"http://example-sim","PassThreshold":0.9}""",
                stored.GetProperty("settingsJson").GetString());
        }
        finally
        {
            await Http.DeleteAsync(Url($"semantic-similarity/{id}"));
        }
    }

    [Fact]
    public async Task Tts_settingsJson_is_canonical_with_every_field_of_its_provider_record()
    {
        var created = await CreateAsync("paragraph-tts", new
        {
            name = $"canon-{Guid.NewGuid():N}",
            type = 3, // Qwen3Base
            settingsJson = """{"top_k":40,"baseUrl":"http://example-tts"}""",
        });
        try
        {
            Assert.Equal(
                """{"baseUrl":"http://example-tts","apiKey":null,"language":"auto","temperature":null,"top_p":null,"top_k":40,"repetition_penalty":null,"max_new_tokens":null,"maxChunkChars":500,"carrierPrefixEnabled":false,"carrierMaxTargetChars":30}""",
                created.GetProperty("settingsJson").GetString());
        }
        finally
        {
            await Http.DeleteAsync(Url($"paragraph-tts/{created.GetProperty("id").GetInt32()}"));
        }
    }

    /// <summary>
    /// Acceptance 1 for every provider type: what the API stores is exactly what Blazor's config
    /// form writes when it opens that config and saves it again.
    /// </summary>
    [Theory]
    [InlineData("paragraph-tts", 0, """{"baseUrl":"http://x","cfg_value":3.5,"maxChunkChars":350}""")]
    [InlineData("paragraph-tts", 1, """{"baseUrl":"http://x","exaggeration":0.9}""")]
    [InlineData("paragraph-tts", 2, """{"baseUrl":"http://x","temperature":1.1}""")]
    [InlineData("paragraph-tts", 3, """{"baseUrl":"http://x","top_k":40,"language":"en"}""")]
    [InlineData("voice-design", 0, """{"baseUrl":"http://x","inference_timesteps":20}""")]
    [InlineData("voice-design", 1, """{"baseUrl":"http://x","apiKey":"k+1","topK":40}""")]
    [InlineData("transcription", 0, """{"baseUrl":"http://x"}""")]
    [InlineData("semantic-similarity", 0, """{"baseUrl":"http://x","passThreshold":0.7}""")]
    [InlineData("semantic-similarity", 1, """{"baseUrl":"http://x","passThreshold":0.6}""")]
    public async Task Stored_settingsJson_is_what_the_Blazor_form_writes_back(string area, int type, string sent)
    {
        var created = await CreateAsync(area, new { name = $"rt-{Guid.NewGuid():N}", type, settingsJson = sent });
        try
        {
            var web = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            var blazor = area switch
            {
                "paragraph-tts" => ParagraphTtsServiceConfigForm.FromConfig(created.Deserialize<ParagraphTtsServiceConfig>(web)!).BuildConfig().SettingsJson,
                "voice-design" => VoiceDesignServiceConfigForm.FromConfig(created.Deserialize<VoiceDesignServiceConfig>(web)!).BuildConfig().SettingsJson,
                "transcription" => TranscriptionServiceConfigForm.FromConfig(created.Deserialize<TranscriptionServiceConfig>(web)!).BuildConfig().SettingsJson,
                _ => SemanticSimilarityServiceConfigForm.FromConfig(created.Deserialize<SemanticSimilarityServiceConfig>(web)!).BuildConfig().SettingsJson,
            };

            var stored = created.GetProperty("settingsJson").GetString()!;
            Assert.Equal(blazor, stored);
            Assert.Contains("http://x", stored);
        }
        finally
        {
            await Http.DeleteAsync(Url($"{area}/{created.GetProperty("id").GetInt32()}"));
        }
    }

    [Fact]
    public async Task SettingsJson_that_is_not_the_providers_shape_is_400()
    {
        var response = await Http.PostAsJsonAsync(Url("transcription"),
            new { name = "bad", type = 0, settingsJson = "{not json" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ── text-processing steps ────────────────────────────────────────────────

    [Fact]
    public async Task Text_steps_lists_the_built_ins_then_the_configs_substitutions()
    {
        var builtIns = (await GetJsonAsync("paragraph-tts/0/text-steps")).EnumerateArray().ToList();
        Assert.NotEmpty(builtIns);
        Assert.All(builtIns, s => Assert.True(s.GetProperty("builtIn").GetBoolean()));
        var sentenceCase = builtIns.Single(s => s.GetProperty("stepId").GetString() == "to-sentence-case");
        Assert.Equal(["paragraphEnabled", "wordEnabled", "wordMinLength"],
            sentenceCase.GetProperty("options").EnumerateArray().Select(o => o.GetProperty("key").GetString()));
        Assert.False(string.IsNullOrEmpty(sentenceCase.GetProperty("label").GetString()));

        var stepId = Guid.NewGuid().ToString();
        var created = await CreateAsync("paragraph-tts", new
        {
            name = $"steps-{Guid.NewGuid():N}",
            type = 0,
            settingsJson = """{"baseUrl":"http://example-tts"}""",
        });
        var id = created.GetProperty("id").GetInt32();
        try
        {
            var update = await Http.PutAsJsonAsync(Url($"paragraph-tts/{id}"), new
            {
                id,
                name = created.GetProperty("name").GetString(),
                type = 0,
                settingsJson = created.GetProperty("settingsJson").GetString(),
                enabledStepIds = new[] { "to-sentence-case", stepId },
                substitutionSteps = new[] { new { id = stepId, fromText = "Dr.", toText = "Doctor", order = 0 } },
                toSentenceCaseConfig = new { paragraphEnabled = true, wordEnabled = false, wordMinLength = 7 },
            });
            Assert.True(update.StatusCode == HttpStatusCode.OK, await update.Content.ReadAsStringAsync());

            var steps = (await GetJsonAsync($"paragraph-tts/{id}/text-steps")).EnumerateArray().ToList();
            Assert.Equal(builtIns.Count + 1, steps.Count);
            var substitution = steps.Last();
            Assert.Equal(stepId, substitution.GetProperty("stepId").GetString());
            Assert.False(substitution.GetProperty("builtIn").GetBoolean());

            // The whole text-processing block reloads as saved.
            var stored = (await GetJsonAsync("paragraph-tts")).EnumerateArray()
                .First(c => c.GetProperty("id").GetInt32() == id);
            Assert.Equal(["to-sentence-case", stepId],
                stored.GetProperty("enabledStepIds").EnumerateArray().Select(s => s.GetString()));
            Assert.Equal("Doctor", stored.GetProperty("substitutionSteps")[0].GetProperty("toText").GetString());
            Assert.Equal(7, stored.GetProperty("toSentenceCaseConfig").GetProperty("wordMinLength").GetInt32());
        }
        finally
        {
            await Http.DeleteAsync(Url($"paragraph-tts/{id}"));
        }

        var missing = await Http.GetAsync(Url("paragraph-tts/987654/text-steps"));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    // ── voice-design sample text ─────────────────────────────────────────────

    [Fact]
    public async Task Sample_text_round_trips_and_the_default_stores_as_none()
    {
        var initial = await GetJsonAsync("voice-design/sample-text");
        var @default = initial.GetProperty("default").GetString();
        Assert.False(string.IsNullOrWhiteSpace(@default));
        try
        {
            var put = await Http.PutAsJsonAsync(Url("voice-design/sample-text"), new { text = "A custom sentence." });
            Assert.Equal(HttpStatusCode.OK, put.StatusCode);
            Assert.Equal("A custom sentence.", (await GetJsonAsync("voice-design/sample-text")).GetProperty("text").GetString());

            // Saving the default text is "no override", as Blazor's page stores it.
            await Http.PutAsJsonAsync(Url("voice-design/sample-text"), new { text = @default });
            Assert.Equal(JsonValueKind.Null, (await GetJsonAsync("voice-design/sample-text")).GetProperty("text").ValueKind);
        }
        finally
        {
            await Http.PutAsJsonAsync(Url("voice-design/sample-text"), new { text = (string?)null });
        }
    }

    // ── test actions ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Voice_design_test_answers_the_designed_audio()
    {
        var id = await FakeConfigIdAsync("voice-design");

        var response = await Http.PostAsJsonAsync(Url($"voice-design/{id}/test"), new { prompt = "A warm old man" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await JsonAsync(response);
        Assert.NotEmpty(Convert.FromBase64String(body.GetProperty("audioBase64").GetString()!));
        Assert.Equal("audio/wav", body.GetProperty("contentType").GetString());
    }

    [Fact]
    public async Task Voice_design_test_needs_a_prompt_and_a_known_config()
    {
        var id = await FakeConfigIdAsync("voice-design");

        var blank = await Http.PostAsJsonAsync(Url($"voice-design/{id}/test"), new { prompt = " " });
        Assert.Equal(HttpStatusCode.BadRequest, blank.StatusCode);

        var missing = await Http.PostAsJsonAsync(Url("voice-design/987654/test"), new { prompt = "x" });
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task A_test_against_a_server_that_is_down_is_422_with_the_reason()
    {
        var created = await CreateAsync("semantic-similarity", new
        {
            name = $"down-{Guid.NewGuid():N}",
            type = 0,
            settingsJson = """{"BaseUrl":"http://no-such-similarity","PassThreshold":0.85}""",
        });
        var id = created.GetProperty("id").GetInt32();
        try
        {
            var response = await Http.PostAsJsonAsync(Url($"semantic-similarity/{id}/test"),
                new { text1 = "a", text2 = "b" });

            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            Assert.False(string.IsNullOrWhiteSpace((await JsonAsync(response)).GetProperty("detail").GetString()));
        }
        finally
        {
            await Http.DeleteAsync(Url($"semantic-similarity/{id}"));
        }
    }

    [Fact]
    public async Task Similarity_test_scores_against_the_configs_threshold()
    {
        var id = await FakeConfigIdAsync("semantic-similarity");

        var response = await Http.PostAsJsonAsync(Url($"semantic-similarity/{id}/test"),
            new { text1 = "The cat sat.", text2 = "A cat was sitting." });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await JsonAsync(response);
        Assert.Equal(1.0, body.GetProperty("score").GetDouble());
        Assert.Equal(0.85, body.GetProperty("threshold").GetDouble());
        Assert.True(body.GetProperty("pass").GetBoolean());

        var blank = await Http.PostAsJsonAsync(Url($"semantic-similarity/{id}/test"), new { text1 = "a", text2 = "" });
        Assert.Equal(HttpStatusCode.BadRequest, blank.StatusCode);
    }

    [Fact]
    public async Task Transcription_test_transcribes_the_uploaded_audio()
    {
        var id = await FakeConfigIdAsync("transcription");

        var response = await Http.PostAsync(Url($"transcription/{id}/test"), AudioForm("clip.wav"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(string.IsNullOrWhiteSpace((await JsonAsync(response)).GetProperty("transcript").GetString()));

        var wrongType = await Http.PostAsync(Url($"transcription/{id}/test"), AudioForm("clip.txt"));
        Assert.Equal(HttpStatusCode.BadRequest, wrongType.StatusCode);

        var missing = await Http.PostAsync(Url("transcription/987654/test"), AudioForm("clip.wav"));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    private static MultipartFormDataContent AudioForm(string fileName)
    {
        var file = new ByteArrayContent([1, 2, 3, 4]);
        file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        return new MultipartFormDataContent { { file, "file", fileName } };
    }
}
