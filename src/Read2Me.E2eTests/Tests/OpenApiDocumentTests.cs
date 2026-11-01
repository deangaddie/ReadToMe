using System.Text.Json.Nodes;
using Read2Me.E2eTests.Infrastructure;

namespace Read2Me.E2eTests.Tests;

/// <summary>
/// Rules over the host's OpenAPI document (#166, #168): enums go on the wire as their member names
/// only, never as integers and never with <c>null</c> as a member (a nullable property is nullable
/// where it is used, not in the enum), and a number is a number, not <c>number | string</c>.
/// </summary>
public class OpenApiDocumentTests(E2eAppFixture app) : IClassFixture<E2eAppFixture>
{
    private async Task<JsonObject> DocumentAsync() =>
        JsonNode.Parse(await OpenApiContractTests.CanonicalDocumentAsync(app.Services))!.AsObject();

    /// <summary>Every schema object in the document with its JSON path.</summary>
    private static IEnumerable<(string Path, JsonObject Schema)> Objects(JsonNode? node, string path = "$")
    {
        switch (node)
        {
            case JsonObject obj:
                yield return (path, obj);
                foreach (var (key, child) in obj)
                foreach (var found in Objects(child, $"{path}.{key}"))
                    yield return found;
                break;
            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                foreach (var found in Objects(array[i], $"{path}[{i}]"))
                    yield return found;
                break;
        }
    }

    private static IEnumerable<string> Types(JsonObject schema) => schema["type"] switch
    {
        JsonValue single => [single.GetValue<string>()],
        JsonArray many => many.Select(t => t!.GetValue<string>()),
        _ => [],
    };

    [Fact]
    public async Task No_enum_is_integer_valued()
    {
        var offenders = Objects(await DocumentAsync())
            .Where(o => o.Schema["enum"] is JsonArray values
                        && (Types(o.Schema).Contains("integer")
                            || values.Any(v => v is JsonValue value && value.GetValueKind() == System.Text.Json.JsonValueKind.Number)))
            .Select(o => o.Path)
            .ToList();

        Assert.True(offenders.Count == 0, "Integer-valued enums:\n  " + string.Join("\n  ", offenders));
    }

    [Fact]
    public async Task No_enum_component_includes_null()
    {
        var schemas = (await DocumentAsync())["components"]?["schemas"]?.AsObject() ?? [];
        var offenders = schemas
            .Where(s => s.Value?["enum"] is JsonArray values && values.Any(v => v is null))
            .Select(s => s.Key)
            .ToList();

        Assert.True(offenders.Count == 0, "Enum components with a null member:\n  " + string.Join("\n  ", offenders));
    }

    [Fact]
    public async Task No_number_also_reads_as_a_string()
    {
        var offenders = Objects(await DocumentAsync())
            .Where(o => Types(o.Schema).ToList() is var types
                        && (types.Contains("integer") || types.Contains("number"))
                        && types.Contains("string"))
            .Select(o => o.Path)
            .ToList();

        Assert.True(offenders.Count == 0, "Numbers that also read as strings:\n  " + string.Join("\n  ", offenders));
    }
}
