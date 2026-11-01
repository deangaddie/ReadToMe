using System.Text.Json;
using System.Threading;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Read2Me.App.Api
{
    /// <summary>
    /// Keeps <c>null</c> out of an enum's members in the OpenAPI document (#166). An enum reached first
    /// through a nullable property (<c>StepAction?</c>) has its one shared component written with
    /// <c>null</c> as a member, which makes every use nullable in <c>schema.d.ts</c>. The property
    /// already says it is nullable (<c>oneOf: [{type: null}, {$ref}]</c>), so the member is dropped.
    /// </summary>
    public sealed class EnumSchemaTransformer : IOpenApiSchemaTransformer
    {
        public Task TransformAsync(OpenApiSchema schema, OpenApiSchemaTransformerContext context, CancellationToken cancellationToken)
        {
            if (schema.Enum is { Count: > 0 } members)
            {
                for (var i = members.Count - 1; i >= 0; i--)
                {
                    if (members[i] is null || members[i]!.GetValueKind() == JsonValueKind.Null)
                        members.RemoveAt(i);
                }
            }
            return Task.CompletedTask;
        }
    }
}
