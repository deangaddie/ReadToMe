using System.Threading;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Read2Me.App.Api
{
    /// <summary>
    /// States the agent API's one failure rule in one place (#168): every <c>/api/</c> operation gets a
    /// <c>default</c> response of <c>ProblemDetails</c> as <c>application/problem+json</c>, since any
    /// non-2xx answer is a problem. Endpoints declare only their 2xx outcomes; the statuses a refusal
    /// can carry are in each operation's summary.
    /// </summary>
    public sealed class ProblemResponseTransformer : IOpenApiDocumentTransformer
    {
        private const string ComponentName = nameof(ProblemDetails);
        private const string ProblemJson = "application/problem+json";

        public async Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
        {
            if (document.Components?.Schemas?.ContainsKey(ComponentName) != true)
                document.AddComponent(ComponentName, await context.GetOrCreateSchemaAsync(typeof(ProblemDetails), null, cancellationToken));

            foreach (var (path, item) in document.Paths)
            {
                if (!path.StartsWith("/api/", StringComparison.Ordinal) || item.Operations is null)
                    continue;
                foreach (var operation in item.Operations.Values)
                {
                    operation.Responses ??= [];
                    operation.Responses["default"] = new OpenApiResponse
                    {
                        Description = "A problem: any non-2xx answer.",
                        Content = new Dictionary<string, OpenApiMediaType>
                        {
                            [ProblemJson] = new() { Schema = new OpenApiSchemaReference(ComponentName, document) },
                        },
                    };
                }
            }
        }
    }
}
