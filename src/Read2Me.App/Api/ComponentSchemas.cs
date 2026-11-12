using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Read2Me.App.Api
{
    /// <summary>
    /// Puts a type no endpoint declares into the document as components, the way the document does for an
    /// endpoint's own types. A document transformer uses it for the contracts outside the endpoint metadata:
    /// the live hub's (#238) and the book commands' (#328).
    /// </summary>
    internal static class ComponentSchemas
    {
        /// <summary>The <paramref name="type"/>'s component, made unless the document already has it.</summary>
        public static async Task AddAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, Type type, CancellationToken ct)
        {
            if (document.Components?.Schemas?.ContainsKey(type.Name) == true)
                return;
            Resolve(document, await context.GetOrCreateSchemaAsync(type, null, ct), type);
        }

        private static readonly NullabilityInfoContext Nullability = new();

        /// <summary>
        /// <c>GetOrCreateSchemaAsync</c> answers with every type it reaches inlined, keeping each one's
        /// <c>x-schema-id</c> only as metadata, so this does what the document does for an endpoint's types:
        /// each identified schema becomes a component (an existing one, such as an HTTP record the hub shares,
        /// is kept) and its uses become references, wrapped as <c>oneOf [null, $ref]</c> where nullable. A
        /// nullable reference-type dictionary value (<c>NodeStatusSummaryDto?</c>, "this node no longer rolls
        /// up") isn't in the metadata, so it is read from the CLR property. Answers the reference to the
        /// schema, or the schema itself when it has no id.
        /// </summary>
        public static IOpenApiSchema Resolve(OpenApiDocument document, IOpenApiSchema schema, Type? clr)
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
        /// value can combine them; the hub sends a list of single members instead (<c>ReceiptEffects</c>).
        /// </summary>
        private static IEnumerable<string> SingleMembers(Type flags) =>
            Enum.GetValues(flags).Cast<Enum>()
                .Where(v => Convert.ToUInt64(v, CultureInfo.InvariantCulture) is var bits && bits != 0 && (bits & (bits - 1)) == 0)
                .Select(v => v.ToString());

        private static object? Meta(OpenApiSchema schema, string key) =>
            schema.Metadata is { } metadata && metadata.TryGetValue(key, out var value) ? value : null;

        private static OpenApiSchema OrNull(IOpenApiSchema schema) =>
            new() { OneOf = [new OpenApiSchema { Type = JsonSchemaType.Null }, schema] };

        private static string JsonName(PropertyInfo property) => JsonName(property.Name);

        /// <summary>A CLR member's name on the wire: camelCase, as the document writes properties.</summary>
        public static string JsonName(string clrName) => JsonNamingPolicy.CamelCase.ConvertName(clrName);

        private static Type? Element(Type? clr) =>
            clr is null ? null : clr.IsArray ? clr.GetElementType() : clr.GetGenericArguments().FirstOrDefault();
    }
}
