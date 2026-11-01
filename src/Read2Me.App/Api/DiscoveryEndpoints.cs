using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Read2Me.App.Characters;
using Read2Me.Core.IO;
using Read2Me.Core.Models;
using Read2Me.Services;
using Read2Me.Services.Characters;
using Read2Me.Services.History;
using Read2Me.Services.Mutations;

namespace Read2Me.App.Api
{
    /// <summary>
    /// <see cref="ExistingCharacterId"/> names the roster character the row resolves onto (by its name,
    /// as apply resolves it) — the review's "Already exists" row.
    /// </summary>
    public sealed record DiscoveredCharacterDto(string Name, IReadOnlyList<string> Aliases, Guid? ExistingCharacterId);
    /// <summary>
    /// <see cref="Collisions"/>: names or aliases two characters would own once every row is
    /// applied (<see cref="AliasCollisions"/>). Advisory; the client recomputes as rows are edited.
    /// </summary>
    public sealed record DiscoveryOutcomeDto(
        DiscoveryStatus Status, string? Reason, IReadOnlyList<DiscoveredCharacterDto> Characters, IReadOnlyList<string> Collisions);
    public sealed record ApplyDiscoveryRow(string Name, IReadOnlyList<string>? Aliases);
    public sealed record ApplyDiscoveryResponse(int Applied);

    public static class DiscoveryEndpoints
    {
        public static void MapDiscoveryEndpoints(this IEndpointRouteBuilder endpoints)
        {
            endpoints.MapPost("/api/projects/{folder}/characters/discover", DiscoverAsync).RefusedWhileSetupCheckRuns()
                .RecordsNoStep("Asks the LLM; nothing is written until the roster is applied.")
                .WithSummary("Ask the active LLM for the book's notable characters and aliases. Synchronous; takes seconds to a minute. Pass ?thinking=true to let the model think first — slower, better recall. Each row carries existingCharacterId when it resolves onto a roster character; collisions lists names two characters would share.");
            endpoints.MapPost("/api/projects/{folder}/characters/discover/apply", ApplyAsync)
                .RecordsStep("Discover characters", StepAction.Character)
                .WithSummary("Persist discovered characters: one create per row (idempotent on name/alias match) plus its aliases.");
        }

        private static async Task<Results<Ok<DiscoveryOutcomeDto>, NotFound, ProblemHttpResult>> DiscoverAsync(
            string folder, bool? thinking, IFileSystem fs, CharacterDiscoveryService discovery,
            ICharacterReader reader, DiscoveryRuns runs, CancellationToken ct)
        {
            if (!ProjectEndpoints.TryResolve(folder, fs, out var folderId))
                return TypedResults.NotFound();

            DiscoveryOutcome outcome;
            // Counted while it runs, so the Setup check's Full check refuses to start meanwhile.
            using (runs.Begin())
                outcome = await discovery.DiscoverAsync(folderId, thinking ?? false, ct);
            if (outcome.Status == DiscoveryStatus.NoLlmConfigured)
                return TypedResults.Problem(outcome.Reason ?? "No active LLM server configured.",
                    statusCode: StatusCodes.Status422UnprocessableEntity);

            // The same resolution the apply step performs, answered up front so the review can
            // mark rows that already exist and warn about names two characters would share.
            var roster = await reader.GetCharactersWithAliasesAsync(folderId);
            var rows = outcome.Characters
                .Select(c => new DiscoveredCharacterDto(
                    c.Name, c.Aliases, CharacterResolver.FindDiscovered(roster, c.Name)?.Id))
                .ToList();
            var collisions = AliasCollisions.Find(
                rows.Select(r => new AliasClaim(r.Name, r.Aliases, r.ExistingCharacterId)),
                roster.Select(c => c.ToAliasOwner()));

            return TypedResults.Ok(new DiscoveryOutcomeDto(
                outcome.Status, outcome.Reason, rows, collisions.Order().ToList()));
        }

        /// Applies the same rows the discovery review dialog applies, through the same seam, so the
        /// agent path and the human one cannot drift: resolve-or-create answers with the existing
        /// character on a name/alias match, and alias adds are deduped by the mutation (ADR 0007).
        private static async Task<Results<Ok<ApplyDiscoveryResponse>, NotFound>> ApplyAsync(
            string folder, IReadOnlyList<ApplyDiscoveryRow> rows, IFileSystem fs,
            CharacterResolver characters, CancellationToken ct)
        {
            if (!ProjectEndpoints.TryResolve(folder, fs, out var folderId))
                return TypedResults.NotFound();

            var applied = 0;
            foreach (var row in rows)
            {
                if (string.IsNullOrWhiteSpace(row.Name))
                    continue;

                if (await characters.ApplyDiscoveredAsync(folderId, row.Name, row.Aliases ?? [], ct)
                    is not BookMutationOutcome.Rejected)
                    applied++;
            }
            return TypedResults.Ok(new ApplyDiscoveryResponse(applied));
        }
    }
}
