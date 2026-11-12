using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Read2Me.App.Live;
using Read2Me.Core.IO;
using Read2Me.Services.BookEdits;

namespace Read2Me.App.Api
{
    /// <param name="Thinking">Let the model think before planning — slower, better on vague instructions and at choosing between template tokens and an LLM; on unless set false.</param>
    public sealed record PlanBookEditRequest(string? Instruction, bool Thinking = true);

    /// <summary>
    /// <paramref name="Status"/> is the planner's <see cref="EditPlanStatus"/>, or <c>NoTargets</c> when the
    /// plan parsed but matched nothing. Only <c>Ok</c> carries a <paramref name="Program"/>, the opaque session
    /// id every later call names. <paramref name="Transform"/> is the plan's <see cref="TransformKind"/>: only
    /// <c>Llm</c> plans send requests per item, so only they have a <paramref name="RequestCount"/> above 0
    /// (the LLM requests the proposal will send) and offer per-row retries.
    /// </summary>
    public sealed record PlanBookEditResponse(
        EditPlanStatus Status,
        string? Reason,
        string? Summary,
        string? Program,
        TransformKind? Transform,
        int TargetCount,
        int RequestCount,
        IReadOnlyList<string> Warnings);

    /// <param name="ConnectionId">The live hub connection that receives the run's <c>bookEdit</c> messages.</param>
    public sealed record ProposeBookEditRequest(bool Thinking = false, string? ConnectionId = null);

    public sealed record ProposeOneBookEditRequest(Guid TargetId, string? Hint = null, bool Thinking = false);

    /// <summary>The session's proposal run as a whole — what a client that missed the hub asks for.</summary>
    public sealed record BookEditRunDto(BookEditRunStatus Status, int Done, int Total, IReadOnlyList<BookEditRowDto> Rows, string? Reason);

    /// <summary>
    /// The AI book-edit flow on the wire (Angular ticket 19): plan → propose → (retry one) → apply.
    /// The plan lives server-side under an opaque <c>program</c> id, so the client holds ids and
    /// rows only; apply is the ordinary <c>ApplyBookEdits</c> command over the rows it kept.
    /// </summary>
    public static class BookEditEndpoints
    {
        public static void MapBookEditEndpoints(this IEndpointRouteBuilder endpoints)
        {
            endpoints.MapPost("/api/projects/{folder}/book-edits/plan", PlanAsync).RefusedWhileSetupCheckRuns()
                .RecordsNoStep("Plans an Edit with AI; nothing commits until it is applied.")
                .WithSummary("Turn a plain-language instruction into an edit plan: one LLM call, then the plan's scope is resolved against the book. Answers status Ok with an opaque program id (valid 2 h after its last use), a summary, the matched item count and, for Llm transforms, the LLM request count; NoLlmConfigured / Unsupported / ServiceUnavailable / Failed / NoTargets carry a reason instead. thinking=true lets the model think first. 409 \"Project is restoring\" while a Restore runs, or when one began after the plan was asked for (its targets would predate it).");
            endpoints.MapPost("/api/projects/{folder}/book-edits/{program}/propose", ProposeAsync).RefusedWhileSetupCheckRuns()
                .RecordsNoStep("Proposes an Edit with AI; nothing commits until it is applied.")
                .WithSummary("Start computing a proposed new value per matched item (202). Progress and the finished rows arrive on /hubs/live as bookEdit { kind: progress | done | failed } on the connectionId in the body; GET the session to read them back. Deterministic transforms finish at once; Llm ones pack up to 8 items and ~12,000 characters of text and sources into each request. 409 while a run is already in flight.");
            endpoints.MapGet("/api/projects/{folder}/book-edits/{program}", GetRun)
                .ReadsLive("an Edit with AI run is a live proposal, not Project state.")
                .WithSummary("The session's proposal run: status Idle | Running | Completed | Cancelled | Failed, progress and the rows landed so far (a cancelled run keeps the rows it computed).");
            endpoints.MapPost("/api/projects/{folder}/book-edits/{program}/propose-one", ProposeOneAsync).RefusedWhileSetupCheckRuns()
                .RecordsNoStep("Proposes an Edit with AI; nothing commits until it is applied.")
                .WithSummary("Ask the AI again for one matched item, optionally steered by a hint; answers the new row. Llm plans only — a deterministic plan answers a Failed row.");
            endpoints.MapPost("/api/projects/{folder}/book-edits/{program}/cancel", Cancel)
                .RecordsNoStep("Stops a proposal; nothing is written.")
                .WithSummary("Stop the running proposal; the rows computed so far stay reviewable. 204 whether or not a run was in flight.");
            endpoints.MapDelete("/api/projects/{folder}/book-edits/{program}", Delete)
                .RecordsNoStep("Drops a proposal; nothing is written.")
                .WithSummary("Drop the session, cancelling any run in flight (204; 404 when unknown or expired).");
        }

        private static async Task<Results<Ok<PlanBookEditResponse>, NotFound, ProblemHttpResult>> PlanAsync(
            string folder, PlanBookEditRequest? body, IFileSystem fs, BookEditPlanService plans, CancellationToken ct)
        {
            if (!ProjectEndpoints.TryResolve(folder, fs, out var folderId))
                return TypedResults.NotFound();
            if (string.IsNullOrWhiteSpace(body?.Instruction))
                return TypedResults.Problem("An instruction is required.", statusCode: StatusCodes.Status400BadRequest);

            return await plans.PlanAsync(folderId, body.Instruction, body.Thinking, ct) switch
            {
                EditPlanResult.Refused refused => StepReads.Refused(refused.Refusal),
                EditPlanResult.NotPlanned notPlanned => TypedResults.Ok(NotPlanned(notPlanned.Status, notPlanned.Reason)),
                EditPlanResult.Planned planned => TypedResults.Ok(new PlanBookEditResponse(
                    EditPlanStatus.Ok, null, planned.Session.Summary, planned.Session.Id,
                    planned.Session.Program.Transform.Kind, planned.Session.Targets.Count,
                    planned.RequestCount, planned.Warnings)),
                _ => throw new InvalidOperationException("Unknown plan result."),
            };
        }

        private static PlanBookEditResponse NotPlanned(EditPlanStatus status, string? reason) =>
            new(status, reason, null, null, null, 0, 0, []);

        private static Results<Accepted<StartedResponse>, NotFound, ProblemHttpResult> ProposeAsync(
            string folder, string program, ProposeBookEditRequest? body, IFileSystem fs,
            IBookEditSessionStore sessions, BookEditRunCoordinator runs)
        {
            if (!ProjectEndpoints.TryResolve(folder, fs, out var folderId))
                return TypedResults.NotFound();
            var session = sessions.TryGet(folderId, program);
            if (session is null)
                return TypedResults.NotFound();

            return runs.TryStart(session, body?.Thinking ?? false, body?.ConnectionId)
                ? TypedResults.Accepted((string?)null, new StartedResponse(true))
                : TypedResults.Problem("A proposal run is already in flight for this plan.", statusCode: StatusCodes.Status409Conflict);
        }

        private static Results<Ok<BookEditRunDto>, NotFound> GetRun(string folder, string program, IFileSystem fs, IBookEditSessionStore sessions)
        {
            if (!ProjectEndpoints.TryResolve(folder, fs, out var folderId))
                return TypedResults.NotFound();
            var session = sessions.TryGet(folderId, program);
            if (session is null)
                return TypedResults.NotFound();

            var run = session.Snapshot();
            return TypedResults.Ok(new BookEditRunDto(
                run.Status, run.Done, run.Total, [.. run.Rows.Select(BookEditRowDto.From)], run.Reason));
        }

        private static async Task<Results<Ok<BookEditRowDto>, NotFound, ProblemHttpResult>> ProposeOneAsync(
            string folder, string program, ProposeOneBookEditRequest? body, IFileSystem fs,
            IBookEditSessionStore sessions, BookEditProposalService proposals, CancellationToken ct)
        {
            if (!ProjectEndpoints.TryResolve(folder, fs, out var folderId))
                return TypedResults.NotFound();
            var session = sessions.TryGet(folderId, program);
            if (session is null)
                return TypedResults.NotFound();
            if (body is null)
                return TypedResults.Problem("A targetId is required.", statusCode: StatusCodes.Status400BadRequest);

            var target = session.FindTarget(body.TargetId);
            if (target is null)
                return TypedResults.Problem("This row no longer maps to an item in the plan.", statusCode: StatusCodes.Status404NotFound);

            var result = await proposals.ProposeOneAsync(
                folderId, session.Program, target, body.Hint, !body.Thinking, ct);
            return TypedResults.Ok(BookEditRowDto.From(result));
        }

        private static Results<NoContent, NotFound> Cancel(string folder, string program, IFileSystem fs, IBookEditSessionStore sessions)
        {
            if (!ProjectEndpoints.TryResolve(folder, fs, out var folderId))
                return TypedResults.NotFound();
            var session = sessions.TryGet(folderId, program);
            if (session is null)
                return TypedResults.NotFound();

            session.CancelRun();
            return TypedResults.NoContent();
        }

        private static Results<NoContent, NotFound> Delete(string folder, string program, IFileSystem fs, IBookEditSessionStore sessions)
        {
            if (!ProjectEndpoints.TryResolve(folder, fs, out var folderId))
                return TypedResults.NotFound();

            return sessions.Remove(folderId, program) ? TypedResults.NoContent() : TypedResults.NotFound();
        }
    }
}
