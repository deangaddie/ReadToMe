using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Read2Me.Core.IO;
using Read2Me.Core.Models;
using Read2Me.App.State;
using Read2Me.Services.Characters;
using Read2Me.Services.UseCases;

namespace Read2Me.App.Api
{
    public sealed record NodeEnqueueRequest(string Level, Guid NodeId, bool UnprocessedOnly = true);
    /// <summary>An explicit selection. Ids that are not Character paragraphs are ignored.</summary>
    public sealed record ParagraphsEnqueueRequest(Guid[] ParagraphIds);
    public sealed record EnqueueResponse(int Enqueued);
    /// <summary>
    /// Queue state only. Attribution results are per item now — read them from the book endpoint's
    /// paragraph items, which carry the stamped character.
    /// </summary>
    public sealed record ParagraphAttributionStatusDto(string? Status, ParagraphOutcome? Outcome);

    public static class AttributionEndpoints
    {
        public static void MapAttributionEndpoints(this IEndpointRouteBuilder endpoints)
        {
            endpoints.MapPost("/api/projects/{folder}/attribution/enqueue", EnqueueAsync)
                .WithSummary("Queue LLM character attribution for the paragraphs under a node (level: volume|part|chapter). Poll /api/attribution/queue.");
            endpoints.MapPost("/api/projects/{folder}/attribution/enqueue-paragraphs", EnqueueParagraphsAsync)
                .WithSummary("Queue LLM character attribution for an explicit list of paragraph ids (a selection). Ids without dialog are ignored; the count answers with what was queued.");
            endpoints.MapGet("/api/projects/{folder}/attribution/paragraphs/{paragraphId:guid}", GetParagraphStatus)
                .WithSummary("Per-paragraph attribution queue state: queued/processing status and any failure/unknown outcome. Results are on the paragraph's items.");
            endpoints.MapDelete("/api/projects/{folder}/attribution/paragraphs/{paragraphId:guid}/outcome", ClearOutcome)
                .WithSummary("Forget a paragraph's Failed/Unfinished attribution outcome (the chip goes away). 204 whether or not one was recorded.");
            endpoints.MapPost("/api/attribution/cancel",
                    (CharacterQueueService queue) => { queue.CancelAll(); return Results.Ok(); })
                .WithSummary("Cancel all queued attribution work.");
            endpoints.MapPost("/api/attribution/dismiss",
                    (AttributionProgressState progress) => { progress.Dismiss(); return Results.Ok(); })
                .WithSummary("Dismiss the finished attribution run's throughput summary (the status dock's Dismiss). 200 whether or not one was showing.");
        }

        private static async Task<IResult> EnqueueAsync(
            string folder, NodeEnqueueRequest request, IFileSystem fs, EnqueueUseCases useCases)
        {
            if (!ProjectEndpoints.TryResolve(folder, fs, out var folderId))
                return Results.NotFound();
            if (!Enum.TryParse<BookNodeLevel>(request.Level, ignoreCase: true, out var level))
                return Results.Problem($"Unknown level '{request.Level}'. Expected volume, part or chapter.",
                    statusCode: StatusCodes.Status400BadRequest);

            var enqueued = await useCases.EnqueueAttributionAsync(folderId, level, request.NodeId, request.UnprocessedOnly);
            return Results.Accepted(value: new EnqueueResponse(enqueued));
        }

        private static async Task<IResult> EnqueueParagraphsAsync(
            string folder, ParagraphsEnqueueRequest request, IFileSystem fs, EnqueueUseCases useCases)
        {
            if (!ProjectEndpoints.TryResolve(folder, fs, out var folderId))
                return Results.NotFound();

            var enqueued = await useCases.EnqueueAttributionAsync(folderId, request.ParagraphIds ?? []);
            return Results.Accepted(value: new EnqueueResponse(enqueued));
        }

        private static IResult ClearOutcome(
            string folder, Guid paragraphId, IFileSystem fs, CharacterQueueService queue)
        {
            if (!ProjectEndpoints.TryResolve(folder, fs, out var folderId))
                return Results.NotFound();

            queue.ClearOutcome(folderId, paragraphId);
            return Results.NoContent();
        }

        private static IResult GetParagraphStatus(
            string folder, Guid paragraphId, IFileSystem fs, CharacterQueueService queue)
        {
            if (!ProjectEndpoints.TryResolve(folder, fs, out var folderId))
                return Results.NotFound();

            return Results.Ok(new ParagraphAttributionStatusDto(
                queue.StatusOf(folderId, paragraphId)?.ToString(),
                queue.OutcomeOf(folderId, paragraphId)));
        }
    }
}
