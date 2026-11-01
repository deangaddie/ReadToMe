using System.Text.Json.Serialization.Metadata;
using System.Threading;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Read2Me.App.Api
{
    /// <summary>
    /// Marks a computed property required in the OpenAPI document (#175). The document makes a property
    /// required only when it is a constructor parameter without a default, so a get-only property
    /// (<c>ProjectSummary.AudioPercent</c>) reads as optional in <c>schema.d.ts</c>, though the serializer
    /// writes it on every answer. A client can't send one (it has no setter and no parameter), so marking
    /// it required never asks anything of a request body.
    /// </summary>
    public sealed class ComputedPropertySchemaTransformer : IOpenApiSchemaTransformer
    {
        public Task TransformAsync(OpenApiSchema schema, OpenApiSchemaTransformerContext context, CancellationToken cancellationToken)
        {
            if (context.JsonTypeInfo.Kind != JsonTypeInfoKind.Object || schema.Properties is not { Count: > 0 } properties)
                return Task.CompletedTask;

            foreach (var property in context.JsonTypeInfo.Properties)
            {
                if (property is { Get: not null, Set: null, AssociatedParameter: null, ShouldSerialize: null }
                    && properties.ContainsKey(property.Name))
                {
                    (schema.Required ??= new HashSet<string>(StringComparer.Ordinal)).Add(property.Name);
                }
            }
            return Task.CompletedTask;
        }
    }
}
