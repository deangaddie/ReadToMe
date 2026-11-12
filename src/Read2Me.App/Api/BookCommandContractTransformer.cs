using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using Read2Me.Core.Models;

namespace Read2Me.App.Api
{
    /// <summary>
    /// Puts every book command into the <c>v1</c> document (#328), so the web client's command union is
    /// generated like the rest of its HTTP types (ADR 0017). The commands endpoint binds a raw
    /// <see cref="JsonObject"/> by hand, so the document can't see the records; this reads the same map
    /// <see cref="BookCommandJson"/> binds from, so the two can't disagree.
    /// <para>
    /// Each record becomes a component under its C# name, with <c>type</c> as a required one-value enum
    /// (the record name minus <c>Command</c>) and without <c>folderId</c>, which the route owns. A
    /// parameter without a default is required, nullable or not, as the binder enforces. The synthetic
    /// <c>BookCommand</c> is the <c>oneOf</c> over them all, discriminated on <c>type</c>, and is the
    /// operation's request body.
    /// </para>
    /// </summary>
    public sealed class BookCommandContractTransformer : IOpenApiDocumentTransformer
    {
        private const string Union = nameof(BookCommand);
        private const string Route = "/api/projects/{folder}/commands";
        private const string Discriminator = "type";

        public async Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
        {
            var mapping = new Dictionary<string, OpenApiSchemaReference>(StringComparer.Ordinal);
            foreach (var (name, type) in BookCommandJson.Commands.OrderBy(c => c.Key, StringComparer.Ordinal))
            {
                var schema = await context.GetOrCreateSchemaAsync(type, null, cancellationToken) as OpenApiSchema
                    ?? throw new InvalidOperationException($"{type.Name} has no inline schema.");
                Shape(schema, name, type);
                ComponentSchemas.Resolve(document, schema, type);
                mapping[name] = new OpenApiSchemaReference(type.Name, document);
            }

            document.AddComponent(Union, new OpenApiSchema
            {
                Description = "Any book command: POST /api/projects/{folder}/commands takes one, chosen by its type.",
                OneOf = [.. mapping.Values],
                Discriminator = new OpenApiDiscriminator { PropertyName = Discriminator, Mapping = mapping },
            });

            var body = document.Paths.TryGetValue(Route, out var path)
                       && path.Operations?.TryGetValue(HttpMethod.Post, out var post) == true
                       && post.RequestBody?.Content?.TryGetValue("application/json", out var json) == true
                ? json
                : throw new InvalidOperationException($"POST {Route} takes no JSON body in the document.");
            body.Schema = new OpenApiSchemaReference(Union, document);
            // The bound JsonObject was its only use.
            document.Components!.Schemas!.Remove(nameof(JsonObject));
        }

        /// <summary>One command's component: <c>type</c> first, no <c>folderId</c>, required as bound.</summary>
        private static void Shape(OpenApiSchema schema, string name, Type type)
        {
            var properties = new Dictionary<string, IOpenApiSchema>(StringComparer.Ordinal)
            {
                [Discriminator] = new OpenApiSchema { Type = JsonSchemaType.String, Enum = [(JsonNode)name] },
            };
            foreach (var (key, value) in schema.Properties ?? new Dictionary<string, IOpenApiSchema>())
            {
                if (key != "folderId")
                    properties[key] = value;
            }
            schema.Properties = properties;

            schema.Required = new HashSet<string>(StringComparer.Ordinal) { Discriminator };
            foreach (var parameter in type.GetConstructors().Single().GetParameters())
            {
                var key = ComponentSchemas.JsonName(parameter.Name!);
                if (!parameter.HasDefaultValue && properties.ContainsKey(key))
                    schema.Required.Add(key);
            }

            var wire = $"Wire type: {name} (the record name minus Command). The folder comes from the route, never the body.";
            schema.Description = string.IsNullOrWhiteSpace(schema.Description) ? wire : $"{schema.Description}\n\n{wire}";
        }
    }
}
