using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Read2Me.App.Services.SetupCheck;

namespace Read2Me.App.Api
{
    /// <summary>
    /// <c>/api/setup-check</c>: the Setup check report, and the Quick check that refreshes it. The
    /// Quick check also runs once as the host starts; it never starts, stops or switches anything.
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
                .WithSummary("Run the Quick check (Workspace, ffmpeg, docker CLI) and answer the new report. It never starts, stops or switches anything; it creates the Workspace folder when missing.");
        }
    }
}
