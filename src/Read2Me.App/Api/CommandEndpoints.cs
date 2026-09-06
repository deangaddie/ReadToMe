using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Read2Me.Core.IO;

namespace Read2Me.App.Api
{
    public sealed record CommandResponse(Guid? NewEntityId);

    public static class CommandEndpoints
    {
        public static void MapCommandEndpoints(this IEndpointRouteBuilder endpoints)
        {
            endpoints.MapPost("/api/projects/{folder}/commands", ExecuteAsync)
                .WithSummary("Execute a book command. Body: { \"type\": \"<Name>\", ...properties }. " +
                             "Type is the command record name without the Command suffix, e.g. CreateCharacter, SetParagraphCharacter.");
        }

        private static async Task<IResult> ExecuteAsync(
            string folder, JsonObject body, IFileSystem fs, BookCommandApiAdapter commands, CancellationToken ct)
        {
            if (!ProjectEndpoints.TryResolve(folder, fs, out var folderId))
                return Results.NotFound();

            var typeName = body["type"]?.GetValue<string>();
            if (string.IsNullOrEmpty(typeName))
                return Results.Problem("Missing 'type' discriminator.", statusCode: StatusCodes.Status400BadRequest);

            if (!BookCommandJson.TryDeserialize(typeName, body, folderId, out var command, out var error))
                return Results.Problem(error, statusCode: StatusCodes.Status400BadRequest);

            return await commands.ExecuteAsync(command!, ct);
        }
    }
}
