using System.Threading;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
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

    public sealed record SetCheckpointRequest(CheckpointRequestState? State);

    /// <summary>A step's confirmed checkpoint as <c>/status</c> reports it.</summary>
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

        private static async Task<IResult> SetAsync(
            string folder, string step, SetCheckpointRequest? body, IFileSystem fs, ProjectUseCases useCases,
            CancellationToken ct)
        {
            if (!ProjectEndpoints.TryResolve(folder, fs, out var folderId) || !TryParseStep(step, out var pipelineStep))
                return Results.NotFound();
            if (!TryReadState(body, out var state))
                return Results.Problem("Expected a JSON body with 'state': Confirmed, Skipped or Open.",
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

        /// <summary>The step's id, exactly as <c>/status</c> keys it; nothing numeric or combined.</summary>
        internal static bool TryParseStep(string segment, out PipelineStep step) =>
            ApiJson.TryParseName(segment, out step);

        /// <summary>
        /// The outcome table. A repeat changed nothing and still answers 204: the pill already shows
        /// that state. A write-lock conflict is 409, so a client can tell "try again" from "never".
        /// </summary>
        internal static IResult ToResult(BookMutationOutcome outcome, CancellationToken ct) => outcome switch
        {
            BookMutationOutcome.Committed or BookMutationOutcome.NoChange => Results.NoContent(),
            BookMutationOutcome.Rejected { Reason: BookMutationRejection.Cancelled } cancelled
                => throw new OperationCanceledException(cancelled.Message, ct),
            BookMutationOutcome.Rejected { Reason: BookMutationRejection.Conflict or BookMutationRejection.Stale or BookMutationRejection.Restoring } conflict
                => Results.Problem(conflict.Message, statusCode: StatusCodes.Status409Conflict),
            BookMutationOutcome.Rejected { Reason: BookMutationRejection.NotFound } missing
                => Results.Problem(missing.Message, statusCode: StatusCodes.Status404NotFound),
            BookMutationOutcome.Rejected rejected
                => Results.Problem(rejected.Message, statusCode: StatusCodes.Status422UnprocessableEntity),
            _ => throw new NotSupportedException($"Unhandled mutation outcome {outcome.GetType().Name}."),
        };
    }
}
