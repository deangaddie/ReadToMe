using System.Text.Json.Serialization.Metadata;
using System.Threading;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Read2Me.App.Api
{
    /// <summary>
    /// Marks a property that isn't a constructor parameter required in the OpenAPI document (#175, #176).
    /// The document makes a property required only when it is a constructor parameter without a default, so
    /// a computed get-only property (<c>ProjectSummary.AudioPercent</c>) and every property of a settable
    /// class (the settings entities, <c>LlmServerConfig</c>) read as optional in <c>schema.d.ts</c>, though
    /// the serializer writes each of them on every answer. A get-only property can't be sent, so it asks
    /// nothing of a request body. A settable one is required of a request body too, where the host would
    /// take the type's default for it: the web client always sends whole entities, and an agent loses only
    /// the licence to leave a field out.
    /// </summary>
    public sealed class AlwaysWrittenPropertySchemaTransformer : IOpenApiSchemaTransformer
    {
        public Task TransformAsync(OpenApiSchema schema, OpenApiSchemaTransformerContext context, CancellationToken cancellationToken)
        {
            if (context.JsonTypeInfo.Kind != JsonTypeInfoKind.Object || schema.Properties is not { Count: > 0 } properties)
                return Task.CompletedTask;

            foreach (var property in context.JsonTypeInfo.Properties)
            {
                if (property is { Get: not null, AssociatedParameter: null, ShouldSerialize: null }
                    && properties.ContainsKey(property.Name))
                {
                    (schema.Required ??= new HashSet<string>(StringComparer.Ordinal)).Add(property.Name);
                }
            }
            return Task.CompletedTask;
        }
    }
}
