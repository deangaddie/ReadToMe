using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Read2Me.E2eTests.Infrastructure;

namespace Read2Me.E2eTests.Tests.Api;

/// <summary>
/// Settings over HTTP: config-area CRUD + active selection, prompt templates,
/// audio-processing scalars.
/// </summary>
[Collection(E2eCollection.Name)]
public class SettingsApiTests(E2eAppFixture app)
{
    private static readonly HttpClient Http = new();

    [Fact]
    public async Task Llm_config_crud_and_active_roundtrip()
    {
        var name = $"cfg-{Guid.NewGuid():N}";

        var create = await Http.PostAsJsonAsync($"{app.BaseUrl}/api/settings/llm",
            new { name, baseUrl = "http://example-llm:1234" });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var created = JsonDocument.Parse(await create.Content.ReadAsStringAsync());
        var id = created.RootElement.GetProperty("id").GetInt32();
        Assert.True(id > 0);

        var list = JsonDocument.Parse(await Http.GetStringAsync($"{app.BaseUrl}/api/settings/llm"));
        Assert.Contains(name, list.RootElement.EnumerateArray().Select(c => c.GetProperty("name").GetString()));

        var update = await Http.PutAsJsonAsync($"{app.BaseUrl}/api/settings/llm/{id}",
            new { id, name, baseUrl = "http://example-llm:9999" });
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);

        var setActive = await Http.PutAsJsonAsync($"{app.BaseUrl}/api/settings/llm/active", new { id });
        Assert.Equal(HttpStatusCode.OK, setActive.StatusCode);

        var active = JsonDocument.Parse(await Http.GetStringAsync($"{app.BaseUrl}/api/settings/llm/active"));
        Assert.Equal(id, active.RootElement.GetProperty("id").GetInt32());
        Assert.Equal("http://example-llm:9999", active.RootElement.GetProperty("baseUrl").GetString());

        // Restore the fake config as active so later tests keep working, then delete ours.
        var fake = JsonDocument.Parse(await Http.GetStringAsync($"{app.BaseUrl}/api/settings/llm"))
            .RootElement.EnumerateArray().First(c => c.GetProperty("name").GetString() == "fake")
            .GetProperty("id").GetInt32();
        await Http.PutAsJsonAsync($"{app.BaseUrl}/api/settings/llm/active", new { id = fake });
        var delete = await Http.DeleteAsync($"{app.BaseUrl}/api/settings/llm/{id}");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
    }

    [Fact]
    public async Task Prompts_get_put_reset_roundtrip()
    {
        var all = JsonDocument.Parse(await Http.GetStringAsync($"{app.BaseUrl}/api/settings/prompts"));
        Assert.True(all.RootElement.TryGetProperty("discover-characters", out var original));
        Assert.False(string.IsNullOrEmpty(original.GetString()));

        var put = await Http.PutAsJsonAsync($"{app.BaseUrl}/api/settings/prompts/discover-characters",
            new { template = "CUSTOM {{book_title}}" });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        var updated = JsonDocument.Parse(await Http.GetStringAsync($"{app.BaseUrl}/api/settings/prompts"));
        Assert.Equal("CUSTOM {{book_title}}", updated.RootElement.GetProperty("discover-characters").GetString());

        var reset = await Http.DeleteAsync($"{app.BaseUrl}/api/settings/prompts/discover-characters");
        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);

        var restored = JsonDocument.Parse(await Http.GetStringAsync($"{app.BaseUrl}/api/settings/prompts"));
        Assert.Equal(original.GetString(), restored.RootElement.GetProperty("discover-characters").GetString());
    }

    [Fact]
    public async Task Unknown_prompt_kind_is_400()
    {
        var response = await Http.PutAsJsonAsync($"{app.BaseUrl}/api/settings/prompts/no-such-kind",
            new { template = "x" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Prompt_catalog_describes_the_eight_kinds_and_tracks_overrides_and_warnings()
    {
        try
        {
            var catalog = JsonDocument.Parse(await Http.GetStringAsync($"{app.BaseUrl}/api/settings/prompts/catalog"));
            var kinds = catalog.RootElement.EnumerateArray().ToList();
            Assert.Equal(
                ["character", "batch-character", "simple-character", "simple-batch-character",
                 "voice-plan", "narrator-voice-plan", "discover-characters", "voice"],
                kinds.Select(k => k.GetProperty("kind").GetString()!).ToArray());

            var character = kinds[0];
            Assert.Equal("Book Character Prompt", character.GetProperty("title").GetString());
            Assert.False(string.IsNullOrEmpty(character.GetProperty("description").GetString()));
            Assert.Contains("narrator_identity",
                character.GetProperty("tokens").EnumerateArray().Select(t => t.GetString()));
            Assert.Contains("\"items\"", character.GetProperty("expectedResponse").GetString());
            Assert.Equal(character.GetProperty("defaultTemplate").GetString(), character.GetProperty("template").GetString());
            Assert.False(character.GetProperty("isOverridden").GetBoolean());
            Assert.Empty(character.GetProperty("warnings").EnumerateArray());
            Assert.Equal(JsonValueKind.Null, kinds[^1].GetProperty("expectedResponse").ValueKind);

            // A stored override without {{narrator_identity}} is flagged; one with it is not.
            var put = await Http.PutAsJsonAsync($"{app.BaseUrl}/api/settings/prompts/character",
                new { template = "Who speaks in {{book_title}}? {{context_json}} {{response_format}}" });
            Assert.Equal(HttpStatusCode.OK, put.StatusCode);
            character = await CatalogEntryAsync("character");
            Assert.True(character.GetProperty("isOverridden").GetBoolean());
            Assert.Equal("Stored override missing {{narrator_identity}}",
                Assert.Single(character.GetProperty("warnings").EnumerateArray()).GetString());
            Assert.Equal("Who speaks in {{book_title}}? {{context_json}} {{response_format}}",
                character.GetProperty("template").GetString());
            Assert.NotEqual(character.GetProperty("defaultTemplate").GetString(), character.GetProperty("template").GetString());

            await Http.PutAsJsonAsync($"{app.BaseUrl}/api/settings/prompts/character",
                new { template = "{{narrator_identity}} {{context_json}}" });
            character = await CatalogEntryAsync("character");
            Assert.Empty(character.GetProperty("warnings").EnumerateArray());

            var reset = await Http.DeleteAsync($"{app.BaseUrl}/api/settings/prompts/character");
            Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
            character = await CatalogEntryAsync("character");
            Assert.False(character.GetProperty("isOverridden").GetBoolean());
        }
        finally
        {
            await Http.DeleteAsync($"{app.BaseUrl}/api/settings/prompts/character");
        }
    }

    [Fact]
    public async Task Prompt_preview_renders_an_unsaved_template_with_the_sample_book()
    {
        var response = await Http.PostAsJsonAsync($"{app.BaseUrl}/api/settings/prompts/voice/preview",
            new { template = "Design a voice for {{character_name}} in {{book_title}} by {{book_author}}. {{not_a_token}}" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var rendered = JsonDocument.Parse(await response.Content.ReadAsStringAsync())
            .RootElement.GetProperty("rendered").GetString();
        Assert.Equal("Design a voice for Gandalf in The Hobbit by J.R.R. Tolkien. {{not_a_token}}", rendered);

        // Each kind previews with its own values: the narrator plan names the narrator.
        var narrator = await Http.PostAsJsonAsync($"{app.BaseUrl}/api/settings/prompts/narrator-voice-plan/preview",
            new { template = "{{character_name}}" });
        Assert.Equal("Narrator", JsonDocument.Parse(await narrator.Content.ReadAsStringAsync())
            .RootElement.GetProperty("rendered").GetString());

        var unknown = await Http.PostAsJsonAsync($"{app.BaseUrl}/api/settings/prompts/no-such-kind/preview",
            new { template = "x" });
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
    }

    private async Task<JsonElement> CatalogEntryAsync(string kind) =>
        JsonDocument.Parse(await Http.GetStringAsync($"{app.BaseUrl}/api/settings/prompts/catalog"))
            .RootElement.EnumerateArray().Single(k => k.GetProperty("kind").GetString() == kind);

    [Fact]
    public async Task Audio_processing_get_and_put()
    {
        var get = JsonDocument.Parse(await Http.GetStringAsync($"{app.BaseUrl}/api/settings/audio-processing"));
        Assert.True(get.RootElement.TryGetProperty("werThreshold", out _));

        var put = await Http.PutAsJsonAsync($"{app.BaseUrl}/api/settings/audio-processing",
            new { werThreshold = 0.25 });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        var updated = JsonDocument.Parse(await Http.GetStringAsync($"{app.BaseUrl}/api/settings/audio-processing"));
        Assert.Equal(0.25, updated.RootElement.GetProperty("werThreshold").GetDouble());
    }

    [Fact]
    public async Task Unknown_settings_area_is_404()
    {
        var response = await Http.GetAsync($"{app.BaseUrl}/api/settings/quantum-flux");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
