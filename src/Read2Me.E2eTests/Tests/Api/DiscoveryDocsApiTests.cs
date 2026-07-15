using System.Net;
using System.Text.Json;
using Read2Me.E2eTests.Infrastructure;

namespace Read2Me.E2eTests.Tests.Api;

/// <summary>
/// Agent discovery surface: the OpenAPI document and the AI-services catalog.
/// </summary>
[Collection(E2eCollection.Name)]
public class DiscoveryDocsApiTests(E2eAppFixture app)
{
    private static readonly HttpClient Http = new();

    [Fact]
    public async Task Openapi_document_lists_agent_endpoints()
    {
        var response = await Http.GetAsync($"{app.BaseUrl}/openapi/v1.json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var paths = doc.RootElement.GetProperty("paths");
        Assert.True(paths.TryGetProperty("/api/projects", out _));
        Assert.True(paths.TryGetProperty("/api/projects/{folder}/commands", out _));
        Assert.True(paths.TryGetProperty("/api/settings/llm", out _));
    }

    [Fact]
    public async Task Ai_services_catalog_lists_managed_containers()
    {
        var response = await Http.GetAsync($"{app.BaseUrl}/api/ai-services");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var names = doc.RootElement.EnumerateArray()
            .Select(s => s.GetProperty("name").GetString()).ToList();
        Assert.Contains("llama", names);
        Assert.Contains("whisper", names);
        Assert.DoesNotContain("whisper-cpu", names);
    }

    [Fact]
    public async Task Ai_service_status_probe_answers()
    {
        var response = await Http.GetAsync($"{app.BaseUrl}/api/ai-services/llama/status");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("Ready", doc.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Unknown_ai_service_is_404()
    {
        var response = await Http.GetAsync($"{app.BaseUrl}/api/ai-services/quantum/status");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
