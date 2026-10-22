using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.DependencyInjection;
using Read2Me.Core.IO;
using Read2Me.Services.History;

namespace Read2Me.App.Api
{
    /// <summary>
    /// <b>View a step</b> for files (spec, "Read side"): <c>/workspace/{folder}/{path}?at=&lt;stepId&gt;</c>
    /// serves a state file — a Voice's reference audio or stored original, the cover, the source
    /// book file — as it was after that step, from the History blobs, so past voices play and past
    /// covers show as they were. The same answers as a project read at <c>?at=</c>: 400 for a value
    /// that isn't a step id, 404 for an unknown step or a file that didn't exist then (any file that
    /// isn't Project state, such as an item's take, never does), 409 while the project is busy.
    /// Without <c>?at=</c> the static mount serves the live file as before.
    /// </summary>
    public static class StepFileReads
    {
        private static readonly FileExtensionContentTypeProvider ContentTypes = new();

        public static IApplicationBuilder UseStateFilesAtStep(this IApplicationBuilder app, PathString workspacePrefix) =>
            app.Use(async (context, next) =>
            {
                if (!context.Request.Query.TryGetValue(StepReads.Parameter, out var at)
                    || !HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method)
                    || !context.Request.Path.StartsWithSegments(workspacePrefix, out var rest))
                {
                    await next(context);
                    return;
                }
                await (await ServeAsync(context, rest, at.ToString())).ExecuteAsync(context);
            });

        private static async Task<IResult> ServeAsync(HttpContext context, PathString rest, string at)
        {
            // "/{folder}/{path}": the folder is one segment, the path the rest, as the rows name it.
            var segments = rest.Value?.TrimStart('/').Split('/', 2);
            if (segments is not [var folder, { Length: > 0 } path]
                || !ProjectEndpoints.TryResolve(folder, context.RequestServices.GetRequiredService<IFileSystem>(), out var folderId))
                return Results.NotFound();
            if (StepReads.RefuseReadAt(context, folderId, at, out var stepId) is { } refusal)
                return refusal;

            var file = await context.RequestServices.GetRequiredService<ProjectHistory>()
                .ReadFileAsync(folderId, stepId, path, context.RequestAborted);
            return file is StepFile.UnknownStep ? StepReads.UnknownStep(stepId) : Serve(context.Response, file, path);
        }

        /// <summary>
        /// Answers a state file read at a step: the file, or 404 when it didn't exist then. A closed
        /// step's file never changes, so it may be cached for good; an open step can still grow, so
        /// its files are never cached. Range-served, like the static mount, so an <c>&lt;audio&gt;</c>
        /// element shows the duration and can seek.
        /// </summary>
        internal static IResult Serve(HttpResponse response, StepFile file, string path)
        {
            if (file is not StepFile.Found found)
                return Results.NotFound();
            response.Headers.CacheControl = found.StepIsOpen ? "no-store" : "private, max-age=31536000, immutable";
            var contentType = ContentTypes.TryGetContentType(path, out var known) ? known : "application/octet-stream";
            return Results.File(found.Path, contentType, enableRangeProcessing: true);
        }
    }
}
