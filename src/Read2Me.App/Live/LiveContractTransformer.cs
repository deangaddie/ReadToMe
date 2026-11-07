using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.AspNetCore.SignalR;
using Microsoft.OpenApi;

namespace Read2Me.App.Live;

/// <summary>
/// Puts the <c>/hubs/live</c> contract into the <c>v1</c> document beside the HTTP one (ADR 0017, #238),
/// so the web client's hub types are generated like its HTTP types. Every <see cref="ILiveClient"/>
/// parameter type and every <see cref="LiveHub"/> return type becomes a component, found by reflection, so
/// a new family lands without anyone listing it. Two synthetic components name the hub's own surface:
/// <c>LiveMessageMap</c> (each <c>[HubMethodName]</c> family → its payload) and <c>LiveHubMethod</c> (the
/// client → server methods).
/// </summary>
public sealed class LiveContractTransformer : IOpenApiDocumentTransformer
{
    public const string MessageMap = "LiveMessageMap";
    public const string HubMethod = "LiveHubMethod";

    /// <summary>Each server → client family by its wire name, with the payload it carries.</summary>
    public static IReadOnlyList<(string Family, Type Payload)> Families { get; } =
    [
        .. typeof(ILiveClient).GetMethods().Select(m => (
            m.GetCustomAttribute<HubMethodNameAttribute>()?.Name
                ?? throw new InvalidOperationException($"ILiveClient.{m.Name} has no [HubMethodName]."),
            m.GetParameters().Single().ParameterType)),
    ];

    /// <summary>The client → server methods <see cref="LiveHub"/> declares; lifecycle overrides aren't callable.</summary>
    public static IReadOnlyList<MethodInfo> HubMethods { get; } =
    [
        .. typeof(LiveHub).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.GetBaseDefinition().DeclaringType == typeof(LiveHub)),
    ];

    /// <summary>What a hub method answers with; null for a bare <see cref="Task"/>.</summary>
    public static Type? Answer(MethodInfo method) => method.ReturnType switch
    {
        { IsGenericType: true } t when t.GetGenericTypeDefinition() == typeof(Task<>) => t.GetGenericArguments()[0],
        var t when t == typeof(Task) || t == typeof(void) => null,
        var t => t,
    };

    public async Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
    {
        var roots = Families.Select(f => f.Payload)
            .Concat(HubMethods.Select(Answer).OfType<Type>())
            .Distinct();
        foreach (var type in roots)
            await AddAsync(document, context, type, cancellationToken);

        var map = new OpenApiSchema
        {
            Type = JsonSchemaType.Object,
            Description = "Each /hubs/live family (connection.on name) and the payload it carries.",
            Properties = new Dictionary<string, IOpenApiSchema>(StringComparer.Ordinal),
            Required = new HashSet<string>(StringComparer.Ordinal),
        };
        foreach (var (family, payload) in Families)
        {
            map.Properties[family] = new OpenApiSchemaReference(payload.Name, document);
            map.Required.Add(family);
        }
        document.AddComponent(MessageMap, map);

        var methods = new OpenApiSchema
        {
            Type = JsonSchemaType.String,
            Description = "The /hubs/live methods a client invokes.",
            Enum = [.. HubMethods.Select(m => (JsonNode)m.Name)],
        };
        document.AddComponent(HubMethod, methods);
    }

    /// <summary>
    /// <c>GetOrCreateSchemaAsync</c> answers with every type it reaches inlined, keeping each one's
    /// <c>x-schema-id</c> only as metadata, so this does what the document does for an endpoint's types:
    /// each identified schema becomes a component (an existing one, such as an HTTP record the hub shares,
    /// is kept) and its uses become references, wrapped as <c>oneOf [null, $ref]</c> where nullable. A
    /// nullable reference-type dictionary value (<c>NodeStatusSummaryDto?</c>, "this node no longer rolls
    /// up") isn't in the metadata, so it is read from the CLR property.
    /// </summary>
    private static async Task AddAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, Type type, CancellationToken ct)
    {
        if (document.Components?.Schemas?.ContainsKey(type.Name) == true)
            return;
        Resolve(document, await context.GetOrCreateSchemaAsync(type, null, ct), type);
    }

    private static readonly NullabilityInfoContext Nullability = new();

    private static IOpenApiSchema Resolve(OpenApiDocument document, IOpenApiSchema schema, Type? clr)
    {
        if (schema is not OpenApiSchema inline)
            return schema;

        var id = Meta(inline, "x-schema-id") as string;
        if (!string.IsNullOrEmpty(id) && document.Components?.Schemas?.ContainsKey(id) == true)
            return Reference(document, id, inline);

        if (clr is { IsEnum: true } && clr.IsDefined(typeof(FlagsAttribute)) && inline.Enum is not { Count: > 0 })
            inline.Enum = [.. SingleMembers(clr).Select(n => (JsonNode)n)];

        if (inline.Properties is { Count: > 0 } properties)
        {
            foreach (var name in properties.Keys.ToList())
            {
                var property = clr?.GetProperties().FirstOrDefault(p => JsonName(p) == name);
                var resolved = Resolve(document, properties[name], property?.PropertyType);
                if (property is not null && resolved is OpenApiSchema { AdditionalProperties: { } values } map
                    && Nullability.Create(property).GenericTypeArguments is [.., { ReadState: NullabilityState.Nullable }])
                {
                    map.AdditionalProperties = OrNull(values);
                }
                properties[name] = resolved;
            }
        }
        if (inline.Items is { } items)
            inline.Items = Resolve(document, items, Element(clr));
        if (inline.AdditionalProperties is { } additional)
            inline.AdditionalProperties = Resolve(document, additional, clr?.GetGenericArguments().LastOrDefault());

        if (string.IsNullOrEmpty(id))
            return inline;
        document.AddComponent(id, inline);
        return Reference(document, id, inline);
    }

    private static IOpenApiSchema Reference(OpenApiDocument document, string id, OpenApiSchema inline)
    {
        var reference = new OpenApiSchemaReference(id, document);
        return Meta(inline, "x-is-nullable-property") is true ? OrNull(reference) : reference;
    }

    /// <summary>
    /// A <c>[Flags]</c> enum's one-bit members. The document writes such an enum as a bare string, since a
    /// value can combine them; the hub sends a list of single members instead (<see cref="ReceiptEffects"/>).
    /// </summary>
    private static IEnumerable<string> SingleMembers(Type flags) =>
        Enum.GetValues(flags).Cast<Enum>()
            .Where(v => Convert.ToUInt64(v, CultureInfo.InvariantCulture) is var bits && bits != 0 && (bits & (bits - 1)) == 0)
            .Select(v => v.ToString());

    private static object? Meta(OpenApiSchema schema, string key) =>
        schema.Metadata is { } metadata && metadata.TryGetValue(key, out var value) ? value : null;

    private static OpenApiSchema OrNull(IOpenApiSchema schema) =>
        new() { OneOf = [new OpenApiSchema { Type = JsonSchemaType.Null }, schema] };

    private static string JsonName(PropertyInfo property) =>
        JsonNamingPolicy.CamelCase.ConvertName(property.Name);

    private static Type? Element(Type? clr) =>
        clr is null ? null : clr.IsArray ? clr.GetElementType() : clr.GetGenericArguments().FirstOrDefault();
}

/// <summary>
/// Marks every property of a hub record required (#238). The hub sends nulls, so every property is on
/// every message; a constructor parameter with a <c>= null</c> default would otherwise read optional.
/// </summary>
public sealed class LiveRecordSchemaTransformer : IOpenApiSchemaTransformer
{
    public Task TransformAsync(OpenApiSchema schema, OpenApiSchemaTransformerContext context, CancellationToken cancellationToken)
    {
        if (context.JsonTypeInfo.Type.Namespace == typeof(LiveHub).Namespace && schema.Properties is { Count: > 0 } properties)
        {
            schema.Required ??= new HashSet<string>(StringComparer.Ordinal);
            foreach (var name in properties.Keys)
                schema.Required.Add(name);
        }
        return Task.CompletedTask;
    }
}
