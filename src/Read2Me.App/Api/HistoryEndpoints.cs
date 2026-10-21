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

    /// <summary>What restoring a step would cost now: the items that would lose their audio.</summary>
    public sealed record RestorePreviewDto(int AudioDiscardCount);

    /// <summary>
    /// One History step as <c>GET /history</c> lists it, plus its <see cref="RestorePreview"/> —
    /// null for a step that can't be opened, and so can't be restored.
    /// </summary>
    public sealed record HistoryStepDto(
        Guid StepId,
        Guid? ParentStepId,
        int Sequence,
        string Label,
        string? Summary,
        string Author,
        DateTimeOffset StartedAt,
        DateTimeOffset? EndedAt,
        bool IsOpen,
        string? Note,
        Guid? RestoredFromStepId,
        bool CantOpen,
        RestorePreviewDto? RestorePreview)
    {
        public static HistoryStepDto From(HistoryStep s, RestorePreviewDto? preview) => new(
            s.StepId, s.ParentStepId, s.Sequence, s.Label, s.Summary, s.Author, s.StartedAt, s.EndedAt, s.IsOpen, s.Note,
            s.RestoredFromStepId, s.CantOpen, preview);
    }

    /// <summary>
    /// A Restore's answer: the Restore step it added — null when the project already was that state,
    /// so nothing changed — and the items whose audio it discarded.
    /// </summary>
    public sealed record RestoreResultDto(HistoryStep? Step, int AudioDiscardCount);

    public static class HistoryEndpoints
    {
        public static void MapHistoryEndpoints(this IEndpointRouteBuilder endpoints)
        {
            endpoints.MapGet("/api/projects/{folder}/history", ListAsync)
                .ReadsLive("History itself is listed as it is now.")
                .Produces<HistoryDto>()
                .WithSummary("The project's History: steps newest first (stepId, parentStepId, display sequence, label, summary — what " +
                             "changed, in user terms, or null — author, startedAt, endedAt — its close, or its last change while open — isOpen while its request or Background run runs, note — \"part 2\", \"212 of 340 paragraphs, cancelled\", \"interrupted\", without brackets — or null, restoredFromStepId — the step a Restore brought back, else null — and cantOpen), headStepId (the tip; null before the first step), busy ({ reason } while " +
                             "anything is queued or running for the project, else null) and restoring (a Restore of the project is running). The first call on a project " +
                             "starts its History. 404 unknown project.");
            endpoints.MapGet("/api/projects/{folder}/history/{stepId:guid}", GetStepAsync)
                .ReadsLive("a step is described as History lists it now.")
                .Produces<HistoryStepDto>()
                .ProducesProblem(StatusCodes.Status404NotFound)
                .ProducesProblem(StatusCodes.Status409Conflict)
                .WithSummary("One History step, in the shape GET /history lists it, plus restorePreview: { audioDiscardCount } — how many " +
                             "items would lose their audio if the project were restored to it now (their text, speaker or delivery cue " +
                             "differs there, or the step doesn't have them) — or null for a step that can't be opened. 404 unknown project " +
                             "or step; 409 while anything is queued or running for the project, or it is restoring (detail says what), " +
                             "since a busy project can't be viewed at a step.");
            endpoints.MapPost("/api/projects/{folder}/history/{stepId:guid}/restore", RestoreAsync)
                .RecordsOwnStep()
                .Produces<RestoreResultDto>()
                .ProducesProblem(StatusCodes.Status404NotFound)
                .ProducesProblem(StatusCodes.Status409Conflict)
                .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
                .WithSummary("Restore: make the project as it stood after the step current, as a new step " +
                             "(\"Restore to 'Split chapter' (14:02)\", restoredFromStepId set) — every later step stays in History. Records keep " +
                             "their ids; items whose text, speaker or delivery cue changes lose their audio and its review, items brought back " +
                             "return without audio, the Pipeline checkpoints it disturbs go stale, and voices, voice reference audio, the cover " +
                             "and the source file come back too; app-wide LLM and TTS settings are untouched. Open Edit with AI sessions for the " +
                             "project are dropped. Answers { step, audioDiscardCount }; step is null when the project already was that state. " +
                             "While it runs every other write and enqueue for the project is 409 \"Project is restoring\". 404 unknown project " +
                             "or step; 409 while anything is queued or running for the project, or it is already restoring (detail says why); " +
                             "422 a step that can't be opened.");
        }

        private static async Task<IResult> ListAsync(
            string folder, IFileSystem fs, ProjectHistory history, ProjectBusyAnswer busy, RestoringProjects restoring, CancellationToken ct)
        {
            if (!ProjectEndpoints.TryResolve(folder, fs, out var folderId))
                return Results.NotFound();

            var list = await history.ListAsync(folderId, ct);
            return Results.Ok(new HistoryDto(
                list.Steps, list.HeadStepId, busy.For(folderId) is { } b ? new HistoryBusyDto(b.Reason) : null,
                restoring.IsRestoring(folderId)));
        }

        /// <summary>The step a client is about to view, with what restoring it would cost.</summary>
        private static async Task<IResult> GetStepAsync(
            string folder, Guid stepId, IFileSystem fs, ProjectHistory history, ProjectBusyAnswer busy, RestoringProjects restoring,
            CancellationToken ct)
        {
            if (!ProjectEndpoints.TryResolve(folder, fs, out var folderId))
                return Results.NotFound();
            if (busy.For(folderId) is { } b)
                return StepReads.Busy(b);
            if (restoring.IsRestoring(folderId))
                return StepReads.Restoring();

            return await history.OpenStepAsync(folderId, stepId, ct) switch
            {
                StepOpening.Opened opened => Results.Ok(HistoryStepDto.From(
                    opened.State.Step, new RestorePreviewDto(await history.CountRestoreAudioDiscardsAsync(folderId, opened.State, ct)))),
                StepOpening.CantOpen cantOpen => Results.Ok(HistoryStepDto.From(cantOpen.Step, preview: null)),
                _ => StepReads.UnknownStep(stepId),
            };
        }

        private static async Task<IResult> RestoreAsync(
            string folder, Guid stepId, IFileSystem fs, HistoryRestore restore, CancellationToken ct)
        {
            if (!ProjectEndpoints.TryResolve(folder, fs, out var folderId))
                return Results.NotFound();

            return await restore.RestoreAsync(folderId, stepId, ct) switch
            {
                RestoreOutcome.Restored restored => Results.Ok(new RestoreResultDto(restored.Step, restored.AudioDiscardCount)),
                RestoreOutcome.UnknownStep => StepReads.UnknownStep(stepId),
                RestoreOutcome.CantOpen => Results.Problem(
                    $"History step {stepId} can't be opened, so it can't be restored.", statusCode: StatusCodes.Status422UnprocessableEntity),
                RestoreOutcome.Refused refused => Results.Problem(refused.Reason, statusCode: StatusCodes.Status409Conflict),
                var other => throw new InvalidOperationException($"Unhandled {other}."),
            };
        }
    }
}
