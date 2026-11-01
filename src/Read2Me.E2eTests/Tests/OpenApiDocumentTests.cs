using System.Reflection;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
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

    /// <summary>
    /// A property that isn't a constructor parameter is on every answer, so the document makes it required
    /// (#175, #176), not optional as it would be by default: a computed get-only one, and every property of
    /// a settable class such as a settings entity, nullable or not.
    /// </summary>
    [Theory]
    [InlineData("ProjectSummary", "audioPercent")]
    [InlineData("AttributionQueueSnapshot", "isBusy")]
    [InlineData("LlmServerConfig", "baseUrl")]
    [InlineData("LlmServerConfig", "temperature")]
    [InlineData("AppTheme", "background")]
    public async Task An_always_written_property_is_required(string component, string property)
    {
        var schema = (await DocumentAsync())["components"]!["schemas"]![component]!;

        Assert.NotNull(schema["properties"]?[property]);
        Assert.Contains(property, schema["required"]!.AsArray().Select(r => r!.GetValue<string>()));
    }

    /// <summary>
    /// A response record's constructor parameters carry no defaults, so every field the serializer writes
    /// reads required (#168, #177), nullable or not; a <c>= null</c> default would make it optional.
    /// </summary>
    [Theory]
    [InlineData("SettingsFieldDto", "min")]
    [InlineData("SettingsFieldDto", "options")]
    [InlineData("SettingsFieldDto", "default")]
    [InlineData("SettingsFieldDto", "help")]
    [InlineData("SettingsOptionDto", "label")]
    [InlineData("StepCatalogEntryDto", "defaults")]
    public async Task A_response_field_is_required(string component, string property)
    {
        var schema = (await DocumentAsync())["components"]!["schemas"]![component]!;

        Assert.NotNull(schema["properties"]?[property]);
        Assert.Contains(property, schema["required"]!.AsArray().Select(r => r!.GetValue<string>()));
    }

    /// <summary>
    /// Every <c>/api/</c> operation states the failure rule: a <c>default</c> response of
    /// <c>ProblemDetails</c> as <c>application/problem+json</c> (#168).
    /// </summary>
    [Fact]
    public async Task Every_api_operation_has_a_default_problem_response()
    {
        var offenders = Operations(await DocumentAsync())
            .Where(o => o.Operation["responses"]?["default"]?["content"]?["application/problem+json"]?["schema"]?["$ref"]
                ?.GetValue<string>() != "#/components/schemas/ProblemDetails")
            .Select(o => o.Key)
            .ToList();

        Assert.True(offenders.Count == 0, "Operations without a default problem response:\n  " + string.Join("\n  ", offenders));
    }

    /// <summary>
    /// A 2xx answer under <c>/api/</c> declares its content (#168): a JSON body from a <c>Results&lt;&gt;</c>
    /// union, or a file's content type. Only a 204, or a bare 202, has no body by design;
    /// an untyped <c>IResult</c> handler shows as a 200 with no content. <see cref="NotYetTyped"/> holds the operations the
    /// endpoint slices still have to convert, and shrinks as they do.
    /// </summary>
    [Fact]
    public async Task Every_2xx_api_response_declares_its_content()
    {
        var undeclared = Operations(await DocumentAsync())
            .Where(o => o.Operation["responses"]?.AsObject() is { } responses
                        && responses.Any(r => r.Key.StartsWith('2') && r.Key is not ("202" or "204") && r.Value?["content"] is null))
            .Select(o => o.Key)
            .ToHashSet();

        var missing = undeclared.Except(NotYetTyped).Order(StringComparer.Ordinal).ToList();
        var stale = NotYetTyped.Except(undeclared).Order(StringComparer.Ordinal).ToList();

        Assert.True(missing.Count == 0 && stale.Count == 0,
            "Operations answering 2xx with undeclared content (return a Results<> union):\n  " + string.Join("\n  ", missing) +
            "\nNotYetTyped entries that are now typed or gone (remove them):\n  " + string.Join("\n  ", stale));
    }

    /// <summary>
    /// The request and response types the endpoints declare, and every type they reach, have distinct short
    /// names (#168): the document names its components by the short CLR name, so two types sharing one would
    /// share one component. The fix is to rename the type that goes on the wire.
    /// </summary>
    [Fact]
    public void No_two_wire_types_share_a_short_name()
    {
        var roots = app.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText?.StartsWith("/api/", StringComparison.Ordinal) == true)
            .SelectMany(e => e.Metadata.OfType<IProducesResponseTypeMetadata>().Select(m => m.Type)
                .Concat(e.Metadata.OfType<IAcceptsMetadata>().Select(m => m.RequestType)))
            .OfType<Type>();

        var seen = new HashSet<Type>();
        foreach (var root in roots)
            Walk(root, seen);

        var collisions = seen
            .GroupBy(t => t.Name, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => $"{g.Key}: {string.Join(", ", g.Select(t => t.FullName))}")
            .ToList();

        Assert.True(collisions.Count == 0, "Wire types sharing a short name:\n  " + string.Join("\n  ", collisions));
    }

    /// <summary>
    /// Adds <paramref name="type"/> and the types its public properties, elements and type arguments reach.
    /// System types are leaves and aren't recorded; framework types are recorded but not walked.
    /// </summary>
    private static void Walk(Type type, HashSet<Type> seen)
    {
        if (Nullable.GetUnderlyingType(type) is { } underlying)
            type = underlying;
        if (type.IsArray)
        {
            Walk(type.GetElementType()!, seen);
            return;
        }
        if (type.IsGenericType)
        {
            foreach (var argument in type.GetGenericArguments())
                Walk(argument, seen);
            if (InNamespace(type, "System"))
                return;
        }
        if (type.IsPrimitive || InNamespace(type, "System") || !seen.Add(type))
            return;
        if (!InNamespace(type, "Read2Me") || type.IsEnum)
            return;
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            Walk(property.PropertyType, seen);
    }

    private static bool InNamespace(Type type, string root) =>
        type.Namespace?.StartsWith(root, StringComparison.Ordinal) == true;

    /// <summary>Every <c>/api/</c> operation, keyed <c>"GET /api/…"</c>.</summary>
    private static IEnumerable<(string Key, JsonObject Operation)> Operations(JsonObject document) =>
        from path in document["paths"]!.AsObject()
        where path.Key.StartsWith("/api/", StringComparison.Ordinal)
        from operation in path.Value!.AsObject()
        where operation.Value is JsonObject { } op && op.ContainsKey("responses")
        select ($"{operation.Key.ToUpperInvariant()} {path.Key}", operation.Value!.AsObject());

    /// <summary>
    /// The operations still answering untyped (an <c>IResult</c> handler), until their endpoint slice
    /// converts them (#175-#180). An entry here that is typed or gone fails the test, so the list only shrinks.
    /// </summary>
    private static readonly HashSet<string> NotYetTyped =
    [
        "DELETE /api/projects/{folder}/assembly/outputs/{fileName}",
        "DELETE /api/projects/{folder}/attribution/paragraphs/{paragraphId}/outcome",
        "DELETE /api/projects/{folder}/book-edits/{program}",
        "GET /api/ai-services",
        "GET /api/ai-services/resolve",
        "GET /api/ai-services/status",
        "GET /api/ai-services/{name}/status",
        "GET /api/assembly/status",
        "GET /api/audio/samples/recent",
        "GET /api/projects/{folder}/assembly/outputs",
        "GET /api/projects/{folder}/assembly/outputs/{fileName}",
        "GET /api/projects/{folder}/attribution/paragraphs/{paragraphId}",
        "GET /api/projects/{folder}/audio/items/{itemId}",
        "GET /api/projects/{folder}/audio/reviews",
        "GET /api/projects/{folder}/book-edits/{program}",
        "GET /api/projects/{folder}/characters",
        "GET /api/projects/{folder}/characters/summary",
        "GET /api/projects/{folder}/characters/{id}/lines",
        "GET /api/projects/{folder}/characters/{id}/voice-rules",
        "GET /api/projects/{folder}/characters/{id}/voice-rules/preview",
        "GET /api/projects/{folder}/nodes/Chapter/{id}/voices",
        "GET /api/projects/{folder}/nodes/{level}/{id}/children",
        "GET /api/projects/{folder}/nodes/{level}/{id}/item-ids",
        "GET /api/projects/{folder}/nodes/{level}/{id}/paragraph-ids",
        "GET /api/projects/{folder}/paragraphs/{paragraphId}/context",
        "GET /api/projects/{folder}/pronunciations",
        "GET /api/settings/audio-processing",
        "GET /api/settings/audio-processing/full",
        "POST /api/ai-services/{name}/restart",
        "POST /api/ai-services/{name}/shutdown",
        "POST /api/ai-services/{name}/start",
        "POST /api/assembly/cancel",
        "POST /api/attribution/cancel",
        "POST /api/attribution/dismiss",
        "POST /api/audio/cancel",
        "POST /api/preflight/{taskKind}/plan",
        "POST /api/preflight/{taskKind}/run",
        "POST /api/projects/{folder}/assembly",
        "POST /api/projects/{folder}/attribution/enqueue",
        "POST /api/projects/{folder}/attribution/enqueue-paragraphs",
        "POST /api/projects/{folder}/audio/enqueue",
        "POST /api/projects/{folder}/audio/enqueue-items",
        "POST /api/projects/{folder}/book-edits/plan",
        "POST /api/projects/{folder}/book-edits/{program}/cancel",
        "POST /api/projects/{folder}/book-edits/{program}/propose",
        "POST /api/projects/{folder}/book-edits/{program}/propose-one",
        "POST /api/projects/{folder}/characters/bulk-assign-preview",
        "POST /api/projects/{folder}/characters/discover",
        "POST /api/projects/{folder}/characters/discover/apply",
        "POST /api/projects/{folder}/commands",
        "POST /api/projects/{folder}/narration/release-preview",
        "POST /api/projects/{folder}/pronunciations/try",
        "POST /api/settings/audio-processing/ffmpeg/test",
        "POST /api/settings/audio-processing/steps/{stepId}/preview",
        "PUT /api/settings/audio-processing",
        "PUT /api/settings/audio-processing/pauses",
        "PUT /api/settings/audio-processing/steps/{stepId}",
    ];
}
