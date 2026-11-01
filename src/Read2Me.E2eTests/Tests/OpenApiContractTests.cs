using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;
using Read2Me.E2eTests.Infrastructure;
using Read2Me.TestUtils;

namespace Read2Me.E2eTests.Tests;

/// <summary>
/// The HTTP contract (ADR 0017): writes the host's OpenAPI document to the checked-in
/// <c>src/Read2Me.Web/src/app/api/openapi.json</c>, which <c>bun run api:types</c> turns into <c>schema.d.ts</c>.
/// <c>scripts/api-types.ps1</c> runs it (the gate's API contract step); it is explicit, so a plain
/// <c>dotnet test</c> and the E2E stages leave the file alone.
/// </summary>
public class OpenApiContractTests(E2eAppFixture app) : IClassFixture<E2eAppFixture>
{
    /// <summary>The checked-in document, relative to the solution's src folder.</summary>
    private const string ContractPath = "Read2Me.Web/src/app/api/openapi.json";

    [Fact(Explicit = true)]
    public async Task Write_the_contract()
    {
        var json = await CanonicalDocumentAsync(app.Services);

        var path = Path.Combine(SourceTree.Root, ContractPath);
        await File.WriteAllTextAsync(path, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// The <c>v1</c> document, in the spec version the host serves, as canonical JSON: object keys sorted
    /// (ordinal) at every depth, <c>servers</c> dropped (it names whatever address the host bound), array
    /// order kept as the host emits it, two-space indent, LF line ends and a final newline. One host
    /// always gives the same bytes.
    /// </summary>
    internal static async Task<string> CanonicalDocumentAsync(IServiceProvider services)
    {
        const string documentName = "v1";
        var provider = services.GetRequiredKeyedService<IOpenApiDocumentProvider>(documentName);
        var version = services.GetRequiredService<IOptionsMonitor<OpenApiOptions>>().Get(documentName).OpenApiVersion;
        var document = await provider.GetOpenApiDocumentAsync(TestContext.Current.CancellationToken);

        var raw = await document.SerializeAsJsonAsync(version, TestContext.Current.CancellationToken);
        var root = JsonNode.Parse(raw)?.AsObject() ?? throw new InvalidOperationException("Empty OpenAPI document.");
        root.Remove("servers");

        var text = Sorted(root)!.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
        return text.ReplaceLineEndings("\n") + "\n";
    }

    private static JsonNode? Sorted(JsonNode? node) => node switch
    {
        JsonObject obj => new JsonObject(obj
            .OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => KeyValuePair.Create(p.Key, Sorted(p.Value)))),
        JsonArray array => new JsonArray([.. array.Select(Sorted)]),
        _ => node?.DeepClone(),
    };
}
