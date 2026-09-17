using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Read2Me.App.Live;
using Read2Me.Core.IO;
using Read2Me.Services.BookEdits;

namespace Read2Me.App.Api
{
    /// <param name="Thinking">Let the model think before planning — slower, better on vague instructions.</param>
    public sealed record PlanBookEditRequest(string? Instruction, bool Thinking = false);

    /// <summary>
    /// <see cref="Status"/> mirrors <see cref="EditPlanStatus"/> plus <c>NoTargets</c> when the plan
    /// parsed but matched nothing. Only <c>Ok</c> carries a <see cref="Program"/>. <see cref="Transform"/>
    /// is the <see cref="TransformKind"/> name: only <c>Llm</c> plans send requests per item, so only
    /// they count <see cref="RequestCount"/> and offer per-row retries.
    /// </summary>
    public sealed record PlanBookEditResponse(
        string Status,
        string? Reason,
        string? Summary,
        string? Program,
        string? Transform,
        int TargetCount,
        int RequestCount,
        IReadOnlyList<string> Warnings);

    /// <param name="ConnectionId">The live hub connection that receives the run's <c>bookEdit</c> messages.</param>
    public sealed record ProposeBookEditRequest(bool Thinking = false, string? ConnectionId = null);

    public sealed record ProposeOneBookEditRequest(Guid TargetId, string? Hint = null, bool Thinking = false);

    /// <summary>The session's proposal run as a whole — what a client that missed the hub asks for.</summary>
    public sealed record BookEditRunDto(string Status, int Done, int Total, IReadOnlyList<BookEditRowDto> Rows, string? Reason);

    /// <summary>
    /// The AI book-edit flow on the wire (Angular ticket 19): plan → propose → (retry one) → apply.
    /// The plan lives server-side under an opaque <c>program</c> id, so the client holds ids and
    /// rows only; apply is the ordinary <c>ApplyBookEdits</c> command over the rows it kept.
    /// </summary>
    public static class BookEditEndpoints
    {
        /// <summary>Above this many LLM-edited items the plan warns that the job is large.</summary>
        public const int LlmTargetWarnThreshold = 300;

        public static void MapBookEditEndpoints(this IEndpointRouteBuilder endpoints)
        {
            endpoints.MapPost("/api/projects/{folder}/book-edits/plan", PlanAsync)
                .WithSummary("Turn a plain-language instruction into an edit plan: one LLM call, then the plan's scope is resolved against the book. Answers status Ok with an opaque program id (valid 2 h after its last use), a summary, the matched item count and, for Llm transforms, the LLM request count; NoLlmConfigured / Unsupported / ServiceUnavailable / Failed / NoTargets carry a reason instead. thinking=true lets the model think first.");
            endpoints.MapPost("/api/projects/{folder}/book-edits/{program}/propose", ProposeAsync)
                .WithSummary("Start computing a proposed new value per matched item (202). Progress and the finished rows arrive on /hubs/live as bookEdit { kind: progress | done | failed } on the connectionId in the body; GET the session to read them back. Deterministic transforms finish at once; Llm ones send one request per 8 items. 409 while a run is already in flight.");
            endpoints.MapGet("/api/projects/{folder}/book-edits/{program}", GetRun)
                .WithSummary("The session's proposal run: status Idle | Running | Completed | Cancelled | Failed, progress and the rows landed so far (a cancelled run keeps the rows it computed).");
            endpoints.MapPost("/api/projects/{folder}/book-edits/{program}/propose-one", ProposeOneAsync)
                .WithSummary("Ask the AI again for one matched item, optionally steered by a hint; answers the new row. Llm plans only — a deterministic plan answers a Failed row.");
            endpoints.MapPost("/api/projects/{folder}/book-edits/{program}/cancel", Cancel)
                .WithSummary("Stop the running proposal; the rows computed so far stay reviewable. 200 whether or not a run was in flight.");
            endpoints.MapDelete("/api/projects/{folder}/book-edits/{program}", Delete)
                .WithSummary("Drop the session, cancelling any run in flight (204; 404 when unknown or expired).");
        }

        private static async Task<IResult> PlanAsync(
            string folder, PlanBookEditRequest? body, IFileSystem fs, BookEditPlanner planner,
            ScopeResolver resolver, IBookEditSessionStore sessions, CancellationToken ct)
        {
            if (!ProjectEndpoints.TryResolve(folder, fs, out var folderId))
                return Results.NotFound();
            if (string.IsNullOrWhiteSpace(body?.Instruction))
                return Results.Problem("An instruction is required.", statusCode: StatusCodes.Status400BadRequest);

            var outcome = await planner.PlanAsync(folderId, body.Instruction, !body.Thinking, ct);
            if (outcome.Status != EditPlanStatus.Ok)
                return Results.Ok(NotPlanned(outcome.Status.ToString(), outcome.Reason));

            var program = outcome.Program!;
            var targets = await resolver.ResolveAsync(folderId, program, ct);
            if (targets.Count == 0)
                return Results.Ok(NotPlanned("NoTargets", "No items in the book match this instruction's scope."));

            var session = sessions.Create(folderId, program, targets, EditProgramDescriber.Describe(program));
            // Read off the service that will send them, so the count cannot drift from the job.
            var batch = BookEditProposalService.BatchSize;
            var requestCount = session.IsLlmTransform ? (targets.Count + batch - 1) / batch : 0;
            var warnings = new List<string>();
            if (session.IsLlmTransform && targets.Count > LlmTargetWarnThreshold)
                warnings.Add($"This is a large AI job ({targets.Count} items). It may take a long time.");

            return Results.Ok(new PlanBookEditResponse(
                "Ok", null, session.Summary, session.Id, program.Transform.Kind.ToString(),
                targets.Count, requestCount, warnings));
        }

        private static PlanBookEditResponse NotPlanned(string status, string? reason) =>
            new(status, reason, null, null, null, 0, 0, []);

        private static IResult ProposeAsync(
            string folder, string program, ProposeBookEditRequest? body, IFileSystem fs,
            IBookEditSessionStore sessions, BookEditRunCoordinator runs)
        {
            if (!ProjectEndpoints.TryResolve(folder, fs, out var folderId))
                return Results.NotFound();
            var session = sessions.TryGet(folderId, program);
            if (session is null)
                return Results.NotFound();

            return runs.TryStart(session, body?.Thinking ?? false, body?.ConnectionId)
                ? Results.Accepted(value: new { started = true })
                : Results.Problem("A proposal run is already in flight for this plan.", statusCode: StatusCodes.Status409Conflict);
        }

        private static IResult GetRun(string folder, string program, IFileSystem fs, IBookEditSessionStore sessions)
        {
            if (!ProjectEndpoints.TryResolve(folder, fs, out var folderId))
                return Results.NotFound();
            var session = sessions.TryGet(folderId, program);
            if (session is null)
                return Results.NotFound();

            var run = session.Snapshot();
            return Results.Ok(new BookEditRunDto(
                run.Status.ToString(), run.Done, run.Total, [.. run.Rows.Select(BookEditRowDto.From)], run.Reason));
        }

        private static async Task<IResult> ProposeOneAsync(
            string folder, string program, ProposeOneBookEditRequest? body, IFileSystem fs,
            IBookEditSessionStore sessions, BookEditProposalService proposals, CancellationToken ct)
        {
            if (!ProjectEndpoints.TryResolve(folder, fs, out var folderId))
                return Results.NotFound();
            var session = sessions.TryGet(folderId, program);
            if (session is null)
                return Results.NotFound();
            if (body is null)
                return Results.Problem("A targetId is required.", statusCode: StatusCodes.Status400BadRequest);

            var target = session.FindTarget(body.TargetId);
            if (target is null)
                return Results.Problem("This row no longer maps to an item in the plan.", statusCode: StatusCodes.Status404NotFound);

            var result = await proposals.ProposeOneAsync(
                folderId, session.Program, target, body.Hint, !body.Thinking, ct);
            return Results.Ok(BookEditRowDto.From(result));
        }

        private static IResult Cancel(string folder, string program, IFileSystem fs, IBookEditSessionStore sessions)
        {
            if (!ProjectEndpoints.TryResolve(folder, fs, out var folderId))
                return Results.NotFound();
            var session = sessions.TryGet(folderId, program);
            if (session is null)
                return Results.NotFound();

            session.CancelRun();
            return Results.Ok();
        }

        private static IResult Delete(string folder, string program, IFileSystem fs, IBookEditSessionStore sessions)
        {
            if (!ProjectEndpoints.TryResolve(folder, fs, out var folderId))
                return Results.NotFound();

            return sessions.Remove(folderId, program) ? Results.NoContent() : Results.NotFound();
        }
    }
}
