using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Read2Me.Core.IO;
using Read2Me.Core.Models;
using Read2Me.Services;
using Read2Me.Services.Characters;

namespace Read2Me.App.Api
{
    public sealed record DiscoveredCharacterDto(string Name, IReadOnlyList<string> Aliases);
    public sealed record DiscoveryOutcomeDto(string Status, string? Reason, IReadOnlyList<DiscoveredCharacterDto> Characters);
    public sealed record ApplyDiscoveryRow(string Name, IReadOnlyList<string>? Aliases);
    public sealed record ApplyDiscoveryResponse(int Applied);

    public static class DiscoveryEndpoints
    {
        public static void MapDiscoveryEndpoints(this IEndpointRouteBuilder endpoints)
        {
            endpoints.MapPost("/api/projects/{folder}/characters/discover", DiscoverAsync)
                .WithSummary("Ask the active LLM for the book's notable characters and aliases. Synchronous; takes seconds to a minute.");
            endpoints.MapPost("/api/projects/{folder}/characters/discover/apply", ApplyAsync)
                .WithSummary("Persist discovered characters: one create per row (idempotent on name/alias match) plus its aliases.");
        }

        private static async Task<IResult> DiscoverAsync(
            string folder, IFileSystem fs, CharacterDiscoveryService discovery, CancellationToken ct)
        {
            if (!ProjectEndpoints.TryResolve(folder, fs, out var folderId))
                return Results.NotFound();

            var outcome = await discovery.DiscoverAsync(folderId, ct);
            if (outcome.Status == DiscoveryStatus.NoLlmConfigured)
                return Results.Problem(outcome.Reason ?? "No active LLM server configured.",
                    statusCode: StatusCodes.Status422UnprocessableEntity);

            return Results.Ok(new DiscoveryOutcomeDto(
                outcome.Status.ToString(),
                outcome.Reason,
                outcome.Characters.Select(c => new DiscoveredCharacterDto(c.Name, c.Aliases)).ToList()));
        }

        /// Same command loop the discovery review dialog runs: create resolves to the
        /// existing character on a name/alias match, alias adds are deduped by the handler.
        private static async Task<IResult> ApplyAsync(
            string folder, IReadOnlyList<ApplyDiscoveryRow> rows, IFileSystem fs,
            IBookCommandHandler handler, CancellationToken ct)
        {
            if (!ProjectEndpoints.TryResolve(folder, fs, out var folderId))
                return Results.NotFound();

            var applied = 0;
            foreach (var row in rows)
            {
                if (string.IsNullOrWhiteSpace(row.Name))
                    continue;

                var characterId = await handler.ExecuteAsync(new CreateCharacterCommand(folderId, row.Name), ct);
                if (characterId is not { } id)
                    continue;

                foreach (var alias in row.Aliases ?? [])
                    await handler.ExecuteAsync(new AddCharacterAliasCommand(folderId, id, alias), ct);
                applied++;
            }
            return Results.Ok(new ApplyDiscoveryResponse(applied));
        }
    }
}
