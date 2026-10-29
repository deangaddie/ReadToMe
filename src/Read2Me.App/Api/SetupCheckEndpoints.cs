using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
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

            group.MapGet("", (ISetupCheckRunner runner) => Results.Ok(runner.Report))
                .Produces<SetupCheckReport>()
                .WithSummary("The Setup check report: one row per check (id, group, name, tier, result, expected, found, note?, fix { text, link?, copy? }), the times of the last Quick and Full check, firstRun (this start created app.db), startedAt (when this host started) and noWorkspace (this host started without a usable Workspace: every other API call answers 503 No Workspace until the app restarts). Empty rows until the start-up Quick check ends.");

            group.MapPost("/quick", async (ISetupCheckRunner runner, CancellationToken ct) =>
                    Results.Ok(await runner.RunQuickCheckAsync(ct)))
                .Produces<SetupCheckReport>()
                .WithSummary("Run the Quick check (Workspace, ffmpeg, docker CLI) and answer the new report. It never starts, stops or switches anything; it creates the Workspace folder when missing. While a Full check runs it answers the run's report instead.");

            group.MapPost("/full", (ISetupCheckRunner runner) =>
                {
                    var start = runner.StartFullCheck();
                    return start.Refusal is { } refusal
                        ? Results.Problem(refusal, statusCode: StatusCodes.Status409Conflict, title: "Work is running")
                        : Results.Accepted(value: start.Report);
                })
                .Produces<SetupCheckReport>(StatusCodes.Status202Accepted)
                .ProducesProblem(StatusCodes.Status409Conflict)
                .WithSummary("Start the Full check in the background, or join the run already going (one per host): 202 with the report, whose fullRun { step, total, lane: local|cpu|gpu, label, cancelling } shows the run. While it runs every queue waits and Preflight refuses new AI tasks with \"Setup check running\". Progress and the final report go to every live hub client as setupCheck { kind: progress|done, report }. 409 naming the work in flight while the Audio or Character queue holds items, or a voice batch, discovery or assembly runs.");

            group.MapPost("/full/cancel", (ISetupCheckRunner runner) =>
                    runner.CancelFullCheck() is { } report
                        ? Results.Accepted(value: report)
                        : Results.Problem("No Full check is running.", statusCode: StatusCodes.Status409Conflict))
                .Produces<SetupCheckReport>(StatusCodes.Status202Accepted)
                .ProducesProblem(StatusCodes.Status409Conflict)
                .WithSummary("Cancel the Full check: it stops once the current step ends, and the checks it hadn't reached read Not run. 202 with the report (fullRun.cancelling true); 409 when none is running.");
        }
    }
}
