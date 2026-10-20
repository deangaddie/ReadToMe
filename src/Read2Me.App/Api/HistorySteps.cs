using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Read2Me.Core.IO;
using Read2Me.Services.History;

namespace Read2Me.App.Api
{
    /// <summary>
    /// Endpoint metadata: this endpoint changes Project state, and its request is one History step
    /// (spec, "Step boundaries"). <see cref="Label"/> names the step; null means the handler opens
    /// the step scope itself, because its label depends on the request (the command endpoint).
    /// </summary>
    public sealed record RecordsHistoryStep(string? Label);

    /// <summary>
    /// Endpoint metadata: this project write is not a History step of its own — it only enqueues a
    /// Background run, writes results or progress, or removes the project — and says why.
    /// </summary>
    public sealed record RecordsNoHistoryStep(string Why);

    /// <summary>
    /// How a project write endpoint declares its place in History. Every non-GET endpoint under
    /// <c>/api/projects/{folder}</c> declares one or the other, so a new write cannot forget to.
    /// </summary>
    public static class HistorySteps
    {
        /// <summary>
        /// Opens a step scope named <paramref name="label"/> around the request, so everything it
        /// commits to the project is one step, and closes it when the handler returns.
        /// </summary>
        public static RouteHandlerBuilder RecordsStep(this RouteHandlerBuilder builder, string label) =>
            builder
                .WithMetadata(new RecordsHistoryStep(label))
                .AddEndpointFilter(async (context, next) =>
                {
                    var http = context.HttpContext;
                    if (http.Request.RouteValues["folder"] is not string folder
                        || !ProjectEndpoints.TryResolve(folder, http.RequestServices.GetRequiredService<IFileSystem>(), out var folderId))
                        return await next(context); // the handler answers the unknown project

                    await using (http.RequestServices.GetRequiredService<HistoryStepScope>().Open(folderId, label))
                        return await next(context);
                });

        /// <summary>The handler opens the step scope itself, with a label it reads from the request.</summary>
        public static RouteHandlerBuilder RecordsOwnStep(this RouteHandlerBuilder builder) =>
            builder.WithMetadata(new RecordsHistoryStep(null));

        public static RouteHandlerBuilder RecordsNoStep(this RouteHandlerBuilder builder, string why) =>
            builder.WithMetadata(new RecordsNoHistoryStep(why));
    }
}
