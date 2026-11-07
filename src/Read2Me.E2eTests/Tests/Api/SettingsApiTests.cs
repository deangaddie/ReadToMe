using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Read2Me.E2eTests.Infrastructure;

namespace Read2Me.E2eTests.Tests.Api;

/// <summary>
/// Settings over HTTP: the prompt catalog and preview as wire shapes, and the unknown-kind 400 (asserted
/// once). Config-area CRUD and active selection are LlmSettingsServiceTests; prompt get / put / reset
/// are LlmPromptServiceTests; the kinds, their copy and tokens are PromptCatalogTests. Audio
/// processing has its own class, AudioProcessingApiTests.
/// </summary>
public class SettingsApiTests(E2eAppFixture app) : IClassFixture<E2eAppFixture>
{
    private static readonly HttpClient Http = new();

    [Fact]
    public async Task Unknown_prompt_kind_is_400()
    {
        var response = await Http.PutAsJsonAsync($"{app.BaseUrl}/api/settings/prompts/no-such-kind",
            new { template = "x" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Prompt_catalog_entries_carry_their_wire_fields_and_a_stored_override_is_flagged()
    {
        try
        {
            var catalog = JsonDocument.Parse(await Http.GetStringAsync($"{app.BaseUrl}/api/settings/prompts/catalog"));
            var character = catalog.RootElement.EnumerateArray()
                .Single(k => k.GetProperty("kind").GetString() == "character");
            foreach (var field in new[] { "title", "description", "expectedResponse", "template", "defaultTemplate" })
                Assert.Equal(JsonValueKind.String, character.GetProperty(field).ValueKind);
            Assert.NotEmpty(character.GetProperty("tokens").EnumerateArray());
            Assert.False(character.GetProperty("isOverridden").GetBoolean());
            Assert.Empty(character.GetProperty("warnings").EnumerateArray());

            // An override without {{narrator_identity}} is flagged (the rule is LlmPromptServiceTests').
            var put = await Http.PutAsJsonAsync($"{app.BaseUrl}/api/settings/prompts/character",
                new { template = "Who speaks in {{book_title}}? {{context_json}} {{response_format}}" });
            Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);
            character = await CatalogEntryAsync("character");
            Assert.True(character.GetProperty("isOverridden").GetBoolean());
            Assert.Single(character.GetProperty("warnings").EnumerateArray());
        }
        finally
        {
            await Http.DeleteAsync($"{app.BaseUrl}/api/settings/prompts/character");
        }
    }

    [Fact]
    public async Task Prompt_preview_renders_an_unsaved_template_in_the_rendered_field()
    {
        var response = await Http.PostAsJsonAsync($"{app.BaseUrl}/api/settings/prompts/voice/preview",
            new { template = "Design a voice for {{character_name}}. {{not_a_token}}" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var rendered = JsonDocument.Parse(await response.Content.ReadAsStringAsync())
            .RootElement.GetProperty("rendered").GetString();
        Assert.DoesNotContain("{{character_name}}", rendered);
        Assert.Contains("{{not_a_token}}", rendered);
    }

    private async Task<JsonElement> CatalogEntryAsync(string kind) =>
        JsonDocument.Parse(await Http.GetStringAsync($"{app.BaseUrl}/api/settings/prompts/catalog"))
            .RootElement.EnumerateArray().Single(k => k.GetProperty("kind").GetString() == kind);

    [Fact]
    public async Task Unknown_settings_area_is_404()
    {
        var response = await Http.GetAsync($"{app.BaseUrl}/api/settings/quantum-flux");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
