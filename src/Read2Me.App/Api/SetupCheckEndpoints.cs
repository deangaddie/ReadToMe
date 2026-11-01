using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Read2Me.App.Services.SetupCheck;

namespace Read2Me.App.Api
{
    /// <summary>
    /// <c>/api/setup-check</c>: the Setup check report, and the Quick check that refreshes it. The
    /// Quick check also runs once as the host starts; it never starts, stops or switches anything. The
    /// Full check runs only when asked, and is joined or cancelled here too.
    /// </summary>
    public static class SetupCheckEndpoints
    {
        public static void MapSetupCheckEndpoints(this IEndpointRouteBuilder endpoints)
        {
            var group = endpoints.MapGroup("/api/setup-check");

            group.MapGet("", (ISetupCheckRunner runner) => TypedResults.Ok(runner.Report))
                .WithSummary("The Setup check report: one row per check (id, group, name, tier, result, expected, found, note, fix { text, link, copy }, managed, rerun), the times of the last Quick and Full check, firstRun (this start created app.db), putBack (after a Full check, what it did with the containers it drove), startedAt (when this host started) and noWorkspace (this host started without a usable Workspace: every other API call answers 503 No Workspace until the app restarts). Empty rows until the start-up Quick check ends.");

            group.MapPost("/quick", async (ISetupCheckRunner runner, CancellationToken ct) =>
                    TypedResults.Ok(await runner.RunQuickCheckAsync(ct)))
                .WithSummary("Run the Quick check (Workspace, ffmpeg, docker CLI) and answer the new report. It never starts, stops or switches anything; it creates the Workspace folder when missing. While a Full check runs it answers the run's report instead.");

            group.MapPost("/full", StartFullAsync)
                .WithSummary("Start the Full check in the background, or join the Full check already going (one per host): 202 with the report, whose fullRun { step, total, lane: local|cpu|gpu, label, cancelling } shows the run. While it runs every queue waits and Preflight refuses new AI tasks with \"Setup check running\". Progress and the final report go to every live hub client as setupCheck { kind: progress|done, report }. 409 naming the work in flight while the Audio or Character queue holds items, or a voice batch, discovery or assembly runs, and while one check re-runs.");

            group.MapPost("/full/cancel", CancelFull)
                .WithSummary("Cancel the Full check: it stops once the current step ends, and the checks it hadn't reached read Not run. 202 with the report (fullRun.cancelling true); 409 when none is running.");

            group.MapPost("/checks/{id}/rerun", RerunAsync)
                .WithSummary("Re-run one Full-tier check (a row whose rerun isn't null) in the background: only the steps that judge it (similarity transcribes first, to score a real transcript), under the Full check's rules — queues wait, Preflight refuses new AI tasks, the containers it drives are put back, and progress goes out as setupCheck messages. rerun.gpu says it stops the running GPU container. The report's fullCheckedAt stays the last whole Full check's. 202 with the report (fullRun shows the re-run); 404 for an unknown or Quick-tier id; 409 while a Full check runs, while work is in flight (named, as for the Full check), before the start-up Quick check ends, when the check has no active config, or when its rerun.gpu is stale (the check now drives a GPU container: run the Quick check first).");
        }

        private static async Task<Results<Accepted<SetupCheckReport>, ProblemHttpResult>> StartFullAsync(ISetupCheckRunner runner)
        {
            var start = await runner.StartFullCheckAsync();
            return start.Refusal is { } refusal
                ? TypedResults.Problem(refusal, statusCode: StatusCodes.Status409Conflict, title: "Work is running")
                : TypedResults.Accepted((string?)null, start.Report);
        }

        private static Results<Accepted<SetupCheckReport>, ProblemHttpResult> CancelFull(ISetupCheckRunner runner) =>
            runner.CancelFullCheck() is { } report
                ? TypedResults.Accepted((string?)null, report)
                : TypedResults.Problem("No Full check is running.", statusCode: StatusCodes.Status409Conflict);

        private static async Task<Results<Accepted<SetupCheckReport>, ProblemHttpResult>> RerunAsync(string id, ISetupCheckRunner runner)
        {
            var start = await runner.RerunCheckAsync(id);
            return start switch
            {
                { Refusal: { } unknown, UnknownCheck: true } =>
                    TypedResults.Problem(unknown, statusCode: StatusCodes.Status404NotFound),
                { Refusal: { } refusal } =>
                    TypedResults.Problem(refusal, statusCode: StatusCodes.Status409Conflict, title: "Can't re-run the check"),
                _ => TypedResults.Accepted((string?)null, start.Report),
            };
        }
    }
}
