using System.Threading;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Read2Me.Core.IO;
using Read2Me.Data.Entities;
using Read2Me.Data.Enums;
using Read2Me.Services.Mutations;
using Read2Me.Services.UseCases;

namespace Read2Me.App.Api
{
    /// <summary>What a checkpoint PUT asks for: a state to hold, or <c>Open</c> to hold none.</summary>
    public enum CheckpointRequestState { Confirmed, Skipped, Open }

    /// <summary><c>PUT /api/projects/{folder}/checkpoints/{step}</c> body: <c>Open</c> deletes the row.</summary>
    public sealed record SetCheckpointRequest(CheckpointRequestState? State);

    /// <summary>
    /// A step's confirmed or skipped checkpoint as <c>/status</c> reports it; <paramref name="At"/> is when,
    /// kept when the row goes stale.
    /// </summary>
    public sealed record StepCheckpointDto(
        CheckpointState State,
        bool Stale,
        DateTimeOffset At);

    public static class CheckpointEndpoints
    {
        public static void MapCheckpointEndpoints(this IEndpointRouteBuilder endpoints)
        {
            endpoints.MapPut("/api/projects/{folder}/checkpoints/{step}", SetAsync)
                .RecordsNoStep("A Pipeline checkpoint is progress, not Project state.")
                .WithSummary("Confirm, skip or reopen a Pipeline step (state: Confirmed | Skipped | Open). 204 on success, " +
                             "including a repeat that changed nothing; the refreshed /status arrives through the Checkpoints receipt. " +
                             "404 unknown step; 422 when the step does not take that state or the book has no content; 400 without a state.");
        }

        private static async Task<Results<NoContent, NotFound, ProblemHttpResult>> SetAsync(
            string folder, string step, SetCheckpointRequest? body, IFileSystem fs, ProjectUseCases useCases,
            CancellationToken ct)
        {
            if (!ProjectEndpoints.TryResolve(folder, fs, out var folderId) || !ApiJson.TryParseName<PipelineStep>(step, out var pipelineStep))
                return TypedResults.NotFound();
            if (!TryReadState(body, out var state))
                return TypedResults.Problem("Expected a JSON body with 'state': Confirmed, Skipped or Open.",
                    statusCode: StatusCodes.Status400BadRequest);

            return ToResult(await useCases.SetCheckpointAsync(folderId.Value, pipelineStep, state), ct);
        }

        /// <summary>The state to hold, null for <c>Open</c>; false when the body or its state is missing.</summary>
        internal static bool TryReadState(SetCheckpointRequest? body, out CheckpointState? state)
        {
            state = body?.State switch
            {
                CheckpointRequestState.Confirmed => CheckpointState.Confirmed,
                CheckpointRequestState.Skipped => CheckpointState.Skipped,
                _ => null,
            };
            return body?.State is not null;
        }

        /// <summary><c>/status</c>'s <c>checkpoints</c>: only steps that have a row, keyed by step (its member name on the wire, as in the route).</summary>
        internal static IReadOnlyDictionary<PipelineStep, StepCheckpointDto> StatusMap(IEnumerable<StepCheckpoint> rows) =>
            rows.ToDictionary(r => r.Step, r => new StepCheckpointDto(r.State, r.Stale, r.At));

        /// <summary>
        /// The outcome table. A repeat changed nothing and still answers 204: the pill already shows
        /// that state. A refusal answers by its Refusal kind, so a client can tell "try again" (409) from "never" (422).
        /// </summary>
        internal static Results<NoContent, NotFound, ProblemHttpResult> ToResult(BookMutationOutcome outcome, CancellationToken ct) => outcome switch
        {
            BookMutationOutcome.Committed or BookMutationOutcome.NoChange => TypedResults.NoContent(),
            BookMutationOutcome.Rejected rejected => MutationRefusalProblem.From(rejected, ct),
            _ => throw new NotSupportedException($"Unhandled mutation outcome {outcome.GetType().Name}."),
        };
    }
}
