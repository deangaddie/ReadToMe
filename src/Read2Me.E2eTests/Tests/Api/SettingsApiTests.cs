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
