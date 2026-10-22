using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;
using Read2Me.App.Queueing;
using Read2Me.Core.IO;
using Read2Me.Core.Models;
using Read2Me.Services;
using Read2Me.Services.History;
using Read2Me.Services.Queueing;

namespace Read2Me.App.Api
{
    /// <summary>
    /// Endpoint metadata: this project read reads Project state, and so can read it as the project
    /// stood after a History step (<c>?at=&lt;stepId&gt;</c>).
    /// </summary>
    public sealed record ReadsAtHistoryStep;

    /// <summary>
    /// Endpoint metadata: this project read answers live results, progress or files, never a past
    /// state, and says why. <c>?at=</c> on it is a 400.
    /// </summary>
    public sealed record ReadsLiveOnly(string Why);

    /// <summary>
    /// <b>View a step</b> on the agent API (spec, "Agent API"): <c>?at=&lt;stepId&gt;</c> on a project
    /// read opens the step and points the request's <see cref="ProjectDbSession"/> at it, so the
    /// handler's readers read the past unchanged. Every GET under <c>/api/projects/{folder}</c>
    /// declares <see cref="ReadsAt"/> or <see cref="ReadsLive"/>; a write refuses <c>?at=</c>, since
    /// writes only ever go to the tip.
    /// </summary>
    public static class StepReads
    {
        public const string Parameter = "at";

        /// <summary>
        /// Accepts <c>?at=&lt;stepId&gt;</c>: 404 for a step the project's History doesn't have, 409
        /// while the project is busy (<c>detail</c> carries the reason), 422 for a step that can't be
        /// opened.
        /// </summary>
        public static RouteHandlerBuilder ReadsAt(this RouteHandlerBuilder builder) =>
            builder
                .WithMetadata(new ReadsAtHistoryStep())
                .ProducesProblem(StatusCodes.Status400BadRequest)
                .ProducesProblem(StatusCodes.Status404NotFound)
                .ProducesProblem(StatusCodes.Status409Conflict)
                .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
                .AddOpenApiOperationTransformer((operation, _, _) =>
                {
                    operation.Parameters ??= [];
                    operation.Parameters.Add(new OpenApiParameter
                    {
                        Name = Parameter,
                        In = ParameterLocation.Query,
                        Required = false,
                        Description = "A History step id: read the project as it stood after that step instead of now. " +
                                      "404 unknown step, 409 while the project is busy (detail says why), 422 a step that can't be opened.",
                        Schema = new OpenApiSchema { Type = JsonSchemaType.String, Format = "uuid" },
                    });
                    return Task.CompletedTask;
                })
                .AddEndpointFilter(async (context, next) =>
                {
                    var http = context.HttpContext;
                    if (!http.Request.Query.TryGetValue(Parameter, out var at))
                        return await next(context);
                    ProjectFolderId? project = null;
                    if (http.Request.RouteValues["folder"] is string folder
                        && ProjectEndpoints.TryResolve(folder, http.RequestServices.GetRequiredService<IFileSystem>(), out var resolved))
                        project = resolved;
                    if (RefuseReadAt(http, project, at.ToString(), out var stepId) is { } refusal)
                        return refusal;
                    if (project is not { } folderId)
                        return await next(context); // the handler answers the unknown project

                    var opening = await http.RequestServices.GetRequiredService<ProjectHistory>()
                        .OpenStepAsync(folderId, stepId, http.RequestAborted);
                    switch (opening)
                    {
                        case StepOpening.UnknownStep:
                            return UnknownStep(stepId);
                        case StepOpening.CantOpen:
                            return Results.Problem($"History step {stepId} can't be opened.", statusCode: StatusCodes.Status422UnprocessableEntity);
                        case StepOpening.Opened opened:
                            http.RequestServices.GetRequiredService<ProjectDbSession>().ReadAt(folderId, opened.State);
                            return await next(context);
                        default:
                            throw new InvalidOperationException($"Unhandled {opening}.");
                    }
                });

        /// <summary>
        /// What every read at <c>?at=</c> checks before it reads the step — a project read and a
        /// state file under <c>/workspace</c> alike: 400 for a value that isn't a step id, 409 while
        /// the project is busy. Null, with the step id, when the read may go ahead; the caller then
        /// answers <see cref="UnknownStep"/> for a step the project's History doesn't have. A null
        /// <paramref name="folder"/> is a project that doesn't exist: only the step id is checked.
        /// </summary>
        internal static IResult? RefuseReadAt(HttpContext http, ProjectFolderId? folder, string at, out Guid stepId)
        {
            if (!Guid.TryParse(at, out stepId))
                return Results.Problem($"'{at}' is not a History step id.", statusCode: StatusCodes.Status400BadRequest);
            return folder is { } project && http.RequestServices.GetRequiredService<ProjectBusyAnswer>().For(project) is { } busy
                ? Busy(busy)
                : null;
        }

        /// <summary>409: a busy project can't be viewed at a step, and <c>detail</c> says what keeps it busy.</summary>
        internal static IResult Busy(ProjectBusy busy) =>
            Results.Problem(busy.Reason, statusCode: StatusCodes.Status409Conflict);

        /// <summary>409: a Restore of the project is running.</summary>
        internal static IResult Restoring() =>
            Results.Problem(RestoringProjects.Reason, statusCode: StatusCodes.Status409Conflict);

        internal static IResult UnknownStep(Guid stepId) =>
            Results.Problem($"The project's History has no step {stepId}.", statusCode: StatusCodes.Status404NotFound);

        /// <summary>A project read of live results, progress or files: refuses <c>?at=</c> with 400.</summary>
        public static RouteHandlerBuilder ReadsLive(this RouteHandlerBuilder builder, string why) =>
            builder.WithMetadata(new ReadsLiveOnly(why)).RefusesAt(why);

        /// <summary>Answers 400 to a request carrying <c>?at=</c>, saying <paramref name="why"/>.</summary>
        internal static RouteHandlerBuilder RefusesAt(this RouteHandlerBuilder builder, string why) =>
            builder.AddEndpointFilter(async (context, next) =>
                context.HttpContext.Request.Query.ContainsKey(Parameter)
                    ? Results.Problem($"?{Parameter}= isn't accepted here: {why}", statusCode: StatusCodes.Status400BadRequest)
                    : await next(context));

        /// <summary>Why a write refuses <c>?at=</c>.</summary>
        internal const string WritesGoToTheTip = "writes only ever go to the current project, never to a History step.";
    }
}
