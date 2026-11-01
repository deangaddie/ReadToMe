using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Read2Me.Core.IO;
using Read2Me.Core.Models;
using Read2Me.App.Live;
using Read2Me.Services.Characters;
using Read2Me.Services.UseCases;

namespace Read2Me.App.Api
{
    public sealed record NodeEnqueueRequest(BookNodeLevel Level, Guid NodeId, bool UnprocessedOnly = true);
    /// <summary>An explicit selection. Ids that are not Character paragraphs are ignored.</summary>
    public sealed record ParagraphsEnqueueRequest(Guid[] ParagraphIds);
    public sealed record EnqueueResponse(int Enqueued)
    {
        /// <summary>
        /// 202 with the count, or 409 with the gate's reason when the queue refused the enqueue. The union is
        /// the enqueue endpoints' own (their unknown-folder 404 included), so a handler returns it as is.
        /// </summary>
        internal static Results<Accepted<EnqueueResponse>, NotFound, ProblemHttpResult> For(EnqueueOutcome outcome) =>
            outcome.Refusal is { } refusal
                ? StepReads.Refused(refusal)
                : TypedResults.Accepted((string?)null, new EnqueueResponse(outcome.Enqueued));
    }
    /// <summary>
    /// Queue state only. Attribution results are per item now — read them from the book endpoint's
    /// paragraph items, which carry the stamped character.
    /// </summary>
    public sealed record ParagraphAttributionStatusDto(ParagraphQueueStatus? Status, ParagraphOutcome? Outcome);

    public static class AttributionEndpoints
    {
        public static void MapAttributionEndpoints(this IEndpointRouteBuilder endpoints)
        {
            endpoints.MapPost("/api/projects/{folder}/attribution/enqueue", EnqueueAsync)
                .RecordsNoStep("Enqueues queue work; what it commits is recorded as it lands.")
                .WithSummary("Queue LLM character attribution for the paragraphs under a node (level: Volume|Part|Chapter). Poll /api/attribution/queue.");
            endpoints.MapPost("/api/projects/{folder}/attribution/enqueue-paragraphs", EnqueueParagraphsAsync)
                .RecordsNoStep("Enqueues queue work; what it commits is recorded as it lands.")
                .WithSummary("Queue LLM character attribution for an explicit list of paragraph ids (a selection). Ids without dialog are ignored; the count answers with what was queued.");
            endpoints.MapGet("/api/projects/{folder}/attribution/paragraphs/{paragraphId:guid}", GetParagraphStatus)
                .ReadsLive("a paragraph's place in the Character Queue is live progress.")
                .WithSummary("Per-paragraph attribution queue state: queued/processing status and any failure/unknown outcome. Results are on the paragraph's items.");
            endpoints.MapDelete("/api/projects/{folder}/attribution/paragraphs/{paragraphId:guid}/outcome", ClearOutcome)
                .RecordsNoStep("Clears the queue's in-memory outcome; nothing is written.")
                .WithSummary("Forget a paragraph's Failed/Unfinished attribution outcome (the chip goes away). 204 whether or not one was recorded.");
            endpoints.MapPost("/api/attribution/cancel",
                    (CharacterQueueService queue) => { queue.CancelAll(); return TypedResults.NoContent(); })
                .WithSummary("Cancel all queued attribution work. 204, also when nothing is queued.");
            endpoints.MapPost("/api/attribution/dismiss",
                    (AttributionProgressState progress) => { progress.Dismiss(); return TypedResults.NoContent(); })
                .WithSummary("Dismiss the finished attribution run's throughput summary (the status dock's Dismiss). 204 whether or not one was showing.");
        }

        private static async Task<Results<Accepted<EnqueueResponse>, NotFound, ProblemHttpResult>> EnqueueAsync(
            string folder, NodeEnqueueRequest request, IFileSystem fs, EnqueueUseCases useCases)
        {
            if (!ProjectEndpoints.TryResolve(folder, fs, out var folderId))
                return TypedResults.NotFound();

            var enqueued = await useCases.EnqueueAttributionAsync(folderId, request.Level, request.NodeId, request.UnprocessedOnly);
            return EnqueueResponse.For(enqueued);
        }

        private static async Task<Results<Accepted<EnqueueResponse>, NotFound, ProblemHttpResult>> EnqueueParagraphsAsync(
            string folder, ParagraphsEnqueueRequest request, IFileSystem fs, EnqueueUseCases useCases)
        {
            if (!ProjectEndpoints.TryResolve(folder, fs, out var folderId))
                return TypedResults.NotFound();

            var enqueued = await useCases.EnqueueAttributionAsync(folderId, request.ParagraphIds ?? []);
            return EnqueueResponse.For(enqueued);
        }

        private static Results<NoContent, NotFound> ClearOutcome(
            string folder, Guid paragraphId, IFileSystem fs, CharacterQueueService queue)
        {
            if (!ProjectEndpoints.TryResolve(folder, fs, out var folderId))
                return TypedResults.NotFound();

            queue.ClearOutcome(folderId, paragraphId);
            return TypedResults.NoContent();
        }

        private static Results<Ok<ParagraphAttributionStatusDto>, NotFound> GetParagraphStatus(
            string folder, Guid paragraphId, IFileSystem fs, CharacterQueueService queue)
        {
            if (!ProjectEndpoints.TryResolve(folder, fs, out var folderId))
                return TypedResults.NotFound();

            return TypedResults.Ok(new ParagraphAttributionStatusDto(
                queue.StatusOf(folderId, paragraphId),
                queue.OutcomeOf(folderId, paragraphId)));
        }
    }
}
