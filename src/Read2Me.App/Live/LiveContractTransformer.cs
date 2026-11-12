using System.Reflection;
using System.Text.Json.Nodes;
using System.Threading;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.AspNetCore.SignalR;
using Microsoft.OpenApi;
using Read2Me.App.Api;

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
            await ComponentSchemas.AddAsync(document, context, type, cancellationToken);

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
