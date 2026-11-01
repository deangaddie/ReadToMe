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
    /// union, or a file's content type. A 200 with no content is an untyped <c>IResult</c> handler; a 201,
    /// 202 or 204 with no content has no body by design. <see cref="NotYetTyped"/> holds the operations the
    /// endpoint slices still have to convert, and shrinks as they do.
    /// </summary>
    [Fact]
    public async Task Every_2xx_api_response_declares_its_content()
    {
        var undeclared = Operations(await DocumentAsync())
            .Where(o => o.Operation["responses"]?.AsObject() is { } responses
                        && responses.Any(r => r.Key == "200" && r.Value?["content"] is null))
            .Select(o => o.Key)
            .ToHashSet();

        var missing = undeclared.Except(NotYetTyped).Order(StringComparer.Ordinal).ToList();
        var stale = NotYetTyped.Except(undeclared).Order(StringComparer.Ordinal).ToList();

        Assert.True(missing.Count == 0 && stale.Count == 0,
            "Operations answering 200 with undeclared content (return a Results<> union):\n  " + string.Join("\n  ", missing) +
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
            if (type.Namespace?.StartsWith("System", StringComparison.Ordinal) == true)
                return;
        }
        if (type.IsPrimitive || type.Namespace?.StartsWith("System", StringComparison.Ordinal) == true || !seen.Add(type))
            return;
        if (type.Namespace?.StartsWith("Read2Me", StringComparison.Ordinal) != true || type.IsEnum)
            return;
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            Walk(property.PropertyType, seen);
    }

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
        "DELETE /api/projects/{folder}",
        "DELETE /api/projects/{folder}/assembly/outputs/{fileName}",
        "DELETE /api/projects/{folder}/attribution/paragraphs/{paragraphId}/outcome",
        "DELETE /api/projects/{folder}/book-edits/{program}",
        "DELETE /api/projects/{folder}/cover",
        "DELETE /api/settings/llm/{id}",
        "DELETE /api/settings/paragraph-tts/{id}",
        "DELETE /api/settings/prompts/{kind}",
        "DELETE /api/settings/semantic-similarity/{id}",
        "DELETE /api/settings/themes/{id}",
        "DELETE /api/settings/transcription/{id}",
        "DELETE /api/settings/voice-design/{id}",
        "GET /api/ai-services",
        "GET /api/ai-services/resolve",
        "GET /api/ai-services/status",
        "GET /api/ai-services/{name}/status",
        "GET /api/assembly/status",
        "GET /api/attribution/queue",
        "GET /api/audio/queue",
        "GET /api/audio/samples/recent",
        "GET /api/audio/steps/catalog",
        "GET /api/previews/{previewId}/{stepId}.wav",
        "GET /api/projects",
        "GET /api/projects/{folder}",
        "GET /api/projects/{folder}/assembly/outputs",
        "GET /api/projects/{folder}/assembly/outputs/{fileName}",
        "GET /api/projects/{folder}/attribution/paragraphs/{paragraphId}",
        "GET /api/projects/{folder}/audio/items/{itemId}",
        "GET /api/projects/{folder}/audio/reviews",
        "GET /api/projects/{folder}/book-edits/{program}",
        "GET /api/projects/{folder}/characters",
        "GET /api/projects/{folder}/characters/summary",
        "GET /api/projects/{folder}/characters/{characterId}/voices",
        "GET /api/projects/{folder}/characters/{id}/lines",
        "GET /api/projects/{folder}/characters/{id}/voice-rules",
        "GET /api/projects/{folder}/characters/{id}/voice-rules/preview",
        "GET /api/projects/{folder}/nodes/Chapter/{id}/voices",
        "GET /api/projects/{folder}/nodes/{level}/{id}/children",
        "GET /api/projects/{folder}/nodes/{level}/{id}/item-ids",
        "GET /api/projects/{folder}/nodes/{level}/{id}/paragraph-ids",
        "GET /api/projects/{folder}/nodes/{level}/{id}/status",
        "GET /api/projects/{folder}/paragraphs/{paragraphId}/context",
        "GET /api/projects/{folder}/pronunciations",
        "GET /api/projects/{folder}/status",
        "GET /api/projects/{folder}/voices/{voiceId}",
        "GET /api/projects/{folder}/voices/{voiceId}/original.wav",
        "GET /api/settings/audio-processing",
        "GET /api/settings/audio-processing/full",
        "GET /api/settings/llm",
        "GET /api/settings/llm/active",
        "GET /api/settings/llm/attribution-chain",
        "GET /api/settings/llm/test",
        "GET /api/settings/paragraph-tts",
        "GET /api/settings/paragraph-tts/active",
        "GET /api/settings/paragraph-tts/schema",
        "GET /api/settings/paragraph-tts/{id}/text-steps",
        "GET /api/settings/prompts",
        "GET /api/settings/prompts/catalog",
        "GET /api/settings/semantic-similarity",
        "GET /api/settings/semantic-similarity/active",
        "GET /api/settings/semantic-similarity/schema",
        "GET /api/settings/themes",
        "GET /api/settings/themes/selection",
        "GET /api/settings/transcription",
        "GET /api/settings/transcription/active",
        "GET /api/settings/transcription/schema",
        "GET /api/settings/voice-design",
        "GET /api/settings/voice-design/active",
        "GET /api/settings/voice-design/sample-text",
        "GET /api/settings/voice-design/schema",
        "GET /api/voice-batch/status",
        "PATCH /api/projects/{folder}",
        "POST /api/ai-services/{name}/restart",
        "POST /api/ai-services/{name}/shutdown",
        "POST /api/ai-services/{name}/start",
        "POST /api/assembly/cancel",
        "POST /api/attribution/cancel",
        "POST /api/attribution/dismiss",
        "POST /api/audio/cancel",
        "POST /api/preflight/{taskKind}/plan",
        "POST /api/preflight/{taskKind}/run",
        "POST /api/projects",
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
        "POST /api/projects/{folder}/characters/{characterId}/design-prompt/generate",
        "POST /api/projects/{folder}/characters/{characterId}/design-prompt/render",
        "POST /api/projects/{folder}/characters/{characterId}/voices/{voiceId}/generate-audio",
        "POST /api/projects/{folder}/commands",
        "POST /api/projects/{folder}/import",
        "POST /api/projects/{folder}/import/manual",
        "POST /api/projects/{folder}/narration/release-preview",
        "POST /api/projects/{folder}/pronunciations/try",
        "POST /api/projects/{folder}/voice-batch/audio",
        "POST /api/projects/{folder}/voice-batch/prompts",
        "POST /api/projects/{folder}/voices/{voiceId}/editor/apply",
        "POST /api/projects/{folder}/voices/{voiceId}/editor/preview",
        "POST /api/projects/{folder}/voices/{voiceId}/editor/restore",
        "POST /api/projects/{folder}/voices/{voiceId}/transcribe",
        "POST /api/settings/audio-processing/ffmpeg/test",
        "POST /api/settings/audio-processing/steps/{stepId}/preview",
        "POST /api/settings/llm",
        "POST /api/settings/llm/models",
        "POST /api/settings/llm/{id}/test",
        "POST /api/settings/llm/{id}/test/cancel",
        "POST /api/settings/paragraph-tts",
        "POST /api/settings/prompts/{kind}/preview",
        "POST /api/settings/semantic-similarity",
        "POST /api/settings/semantic-similarity/{id}/test",
        "POST /api/settings/themes",
        "POST /api/settings/transcription",
        "POST /api/settings/transcription/{id}/test",
        "POST /api/settings/voice-design",
        "POST /api/settings/voice-design/{id}/test",
        "POST /api/voice-batch/cancel",
        "PUT /api/projects/{folder}/checkpoints/{step}",
        "PUT /api/projects/{folder}/cover",
        "PUT /api/projects/{folder}/narrator-only-mode",
        "PUT /api/projects/{folder}/voices/{voiceId}/audio",
        "PUT /api/settings/audio-processing",
        "PUT /api/settings/audio-processing/pauses",
        "PUT /api/settings/audio-processing/steps/{stepId}",
        "PUT /api/settings/llm/active",
        "PUT /api/settings/llm/attribution-chain",
        "PUT /api/settings/llm/{id}",
        "PUT /api/settings/paragraph-tts/active",
        "PUT /api/settings/paragraph-tts/{id}",
        "PUT /api/settings/prompts/{kind}",
        "PUT /api/settings/semantic-similarity/active",
        "PUT /api/settings/semantic-similarity/{id}",
        "PUT /api/settings/themes/selection",
        "PUT /api/settings/themes/{id}",
        "PUT /api/settings/transcription/active",
        "PUT /api/settings/transcription/{id}",
        "PUT /api/settings/voice-design/active",
        "PUT /api/settings/voice-design/sample-text",
        "PUT /api/settings/voice-design/{id}",
    ];
}
