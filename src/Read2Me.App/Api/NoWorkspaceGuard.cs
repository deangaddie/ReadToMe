using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Read2Me.App.Services.SetupCheck;

namespace Read2Me.App.Api
{
    /// <summary>
    /// No-Workspace mode: every API call except the Setup check's, and every Workspace-backed file
    /// route, answers one "no Workspace" problem (503), so an agent can tell the cause at once.
    /// </summary>
    public static class NoWorkspaceGuard
    {
        public const string Title = "No Workspace";

        private static readonly PathString[] Guarded = ["/api", "/workspace", "/audio-preview", "/preview-source"];
        private static readonly PathString SetupCheck = "/api/setup-check";

        public static IApplicationBuilder UseNoWorkspaceGuard(this IApplicationBuilder app, WorkspaceVerdict verdict) =>
            app.Use(async (context, next) =>
            {
                var path = context.Request.Path;
                if (!Guarded.Any(p => path.StartsWithSegments(p)) || path.StartsWithSegments(SetupCheck))
                {
                    await next(context);
                    return;
                }

                await Results.Problem(
                    $"The app started without a usable Workspace ({verdict.Found}). GET {SetupCheck} for the fix, then restart the app.",
                    statusCode: StatusCodes.Status503ServiceUnavailable,
                    title: Title).ExecuteAsync(context);
            });
    }
}
