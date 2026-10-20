using System.Threading;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Read2Me.App.Queueing;
using Read2Me.Core.IO;
using Read2Me.Services.History;

namespace Read2Me.App.Api
{
    /// <summary>Why the project can't be viewed at a step or restored right now: what is queued or running for it.</summary>
    public sealed record HistoryBusyDto(string Reason);

    /// <summary>
    /// A project's History: its steps newest first, the step the Head points at, what keeps it busy
    /// (null when idle) and whether a Restore is in progress.
    /// </summary>
    public sealed record HistoryDto(
        IReadOnlyList<HistoryStep> Steps, Guid? HeadStepId, HistoryBusyDto? Busy, bool Restoring);

    public static class HistoryEndpoints
    {
        public static void MapHistoryEndpoints(this IEndpointRouteBuilder endpoints)
        {
            endpoints.MapGet("/api/projects/{folder}/history", ListAsync)
                .Produces<HistoryDto>()
                .WithSummary("The project's History: steps newest first (stepId, parentStepId, display sequence, label, summary — what " +
                             "changed, in user terms, or null — author, startedAt, endedAt — its close, or its last change while open — isOpen while its request or Background run runs, note — \"part 2\", \"212 of 340 paragraphs, cancelled\", \"interrupted\", without brackets — or null, restoredFromStepId — the step a Restore brought back, else null — and cantOpen), headStepId (the tip; null before the first step), busy ({ reason } while " +
                             "anything is queued or running for the project, else null) and restoring. The first call on a project " +
                             "starts its History. 404 unknown project.");
        }

        private static async Task<IResult> ListAsync(
            string folder, IFileSystem fs, ProjectHistory history, ProjectBusyAnswer busy, CancellationToken ct)
        {
            if (!ProjectEndpoints.TryResolve(folder, fs, out var folderId))
                return Results.NotFound();

            var list = await history.ListAsync(folderId, ct);
            // Restore is not built yet, so a project is never restoring.
            return Results.Ok(new HistoryDto(
                list.Steps, list.HeadStepId, busy.For(folderId) is { } b ? new HistoryBusyDto(b.Reason) : null, Restoring: false));
        }
    }
}
