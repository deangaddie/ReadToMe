using System.Net;
using System.Text.Json;
using Read2Me.E2eTests.Infrastructure;

namespace Read2Me.E2eTests.Tests.Api;

/// <summary>
/// Provider settings schemas: the wire shape of one descriptor. Its 400s (an unknown type, the removed
/// Chatterbox Turbo) are asserted once, in <c>ProviderSettingsApiTests</c>.
/// </summary>
public class ProviderSchemaApiTests(E2eAppFixture app) : IClassFixture<E2eAppFixture>
{
    private static readonly HttpClient Http = new();

    /// <summary>
    /// The endpoint's wire shape, on one provider. Every provider's fields keep the same rules in
    /// ProviderSettingsSchemaTests.
    /// </summary>
    [Fact]
    public async Task Schema_describes_a_provider_type()
    {
        const string type = "VoxCpm2";
        var response = await Http.GetAsync($"{app.BaseUrl}/api/settings/paragraph-tts/schema?type={type}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var schema = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(type, schema.GetProperty("type").GetString());
        var fields = schema.GetProperty("fields").EnumerateArray().ToList();
        Assert.NotEmpty(fields);
        var keys = fields.Select(f => f.GetProperty("key").GetString()).ToList();
        Assert.Contains("cfg_value", keys);
        foreach (var field in fields)
        {
            Assert.False(string.IsNullOrEmpty(field.GetProperty("label").GetString()));
            Assert.Contains(field.GetProperty("kind").GetString(), new[] { "number", "boolean", "enum", "string", "text" });
            // Every field either has a recommended default or is explicitly nullable ("server default").
            Assert.True(field.GetProperty("default").ValueKind != JsonValueKind.Null
                        || field.GetProperty("nullable").GetBoolean());
        }
        // Connection and app-level chunking knobs are per-config, never per-voice.
        Assert.DoesNotContain("baseUrl", keys);
        Assert.DoesNotContain("maxChunkChars", keys);
    }
}
