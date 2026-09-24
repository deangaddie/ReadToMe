using System.Net;
using System.Text.Json;
using Read2Me.E2eTests.Infrastructure;

namespace Read2Me.E2eTests.Tests.Api;

/// <summary>Provider settings schemas (Angular ticket 16): one descriptor per provider type.</summary>
[Collection(E2eCollection.Name)]
public class ProviderSchemaApiTests(E2eAppFixture app)
{
    private static readonly HttpClient Http = new();

    [Theory]
    [InlineData("paragraph-tts", "VoxCpm2", "cfg_value")]
    [InlineData("paragraph-tts", "Chatterbox", "exaggeration")]
    [InlineData("paragraph-tts", "ChatterboxTurbo", "temperature")]
    [InlineData("paragraph-tts", "Qwen3Base", "language")]
    [InlineData("voice-design", "VoxCpm2", "cfg_value")]
    [InlineData("voice-design", "Qwen3", "language")]
    public async Task Schema_describes_every_provider_type(string area, string type, string expectedKey)
    {
        var response = await Http.GetAsync($"{app.BaseUrl}/api/settings/{area}/schema?type={type}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var schema = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(type, schema.GetProperty("type").GetString());
        var fields = schema.GetProperty("fields").EnumerateArray().ToList();
        Assert.NotEmpty(fields);
        var keys = fields.Select(f => f.GetProperty("key").GetString()).ToList();
        Assert.Contains(expectedKey, keys);
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

    [Fact]
    public async Task Schema_accepts_the_numeric_enum_value_and_rejects_unknown_types()
    {
        var numeric = JsonDocument.Parse(
            await Http.GetStringAsync($"{app.BaseUrl}/api/settings/paragraph-tts/schema?type=1")).RootElement;
        Assert.Equal("Chatterbox", numeric.GetProperty("type").GetString());

        var unknown = await Http.GetAsync($"{app.BaseUrl}/api/settings/voice-design/schema?type=Nope");
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);

        var missing = await Http.GetAsync($"{app.BaseUrl}/api/settings/paragraph-tts/schema");
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
    }
}
