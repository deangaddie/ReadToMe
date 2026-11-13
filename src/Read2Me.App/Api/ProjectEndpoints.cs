using System.Threading;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Read2Me.Core.IO;
using Read2Me.Core.Models;
using Read2Me.Data;
using Read2Me.Data.Enums;
using Read2Me.Services;
using Read2Me.Services.History;
using Read2Me.Services.Mutations;
using Read2Me.Services.Series;
using Read2Me.Services.UseCases;

namespace Read2Me.App.Api
{
    public static class ProjectEndpoints
    {
        public static void MapProjectEndpoints(this IEndpointRouteBuilder endpoints)
        {
            endpoints.MapGet("/api/projects", ListAsync)
                .WithSummary("List all projects with audio progress counters.");
            endpoints.MapPost("/api/projects", CreateAsync)
                .WithSummary("Create a project from an uploaded book file (multipart: title, bookTitle, author, file).");
            endpoints.MapGet("/api/projects/{folder}", GetAsync)
                .ReadsAt()
                .WithSummary("Project metadata for one folder.");
            endpoints.MapDelete("/api/projects/{folder}", DeleteAsync)
                .RecordsNoStep("Removes the project, History and all.")
                .WithSummary("Delete a project folder and everything in it.");
            endpoints.MapPost("/api/projects/{folder}/import", ImportAsync)
                .RecordsOwnStep()
                .WithSummary("Read the stored book file into volumes/chapters/paragraphs. reread=true clears existing content first. 204 on success. " +
                             "An X-Origin-Id header (GUID) is echoed as originId on the mutation receipt the live hub publishes.");
            endpoints.MapPost("/api/projects/{folder}/import/manual", ImportManuallyAsync)
                .RecordsStep("Re-split book", StepAction.Split)
                .WithSummary("Re-split the stored book file by hand-chosen rules (replaces existing content); 204 on success. " +
                             "Per level: mode Prefix (with prefix) | Arabic | Roman. 400 when a switched-on level lacks a valid rule. " +
                             "An X-Origin-Id header (GUID) is echoed as originId on the mutation receipt the live hub publishes.");
            endpoints.MapPatch("/api/projects/{folder}", UpdateAsync)
                .RecordsStep("Edit project details", StepAction.Edit)
                .WithSummary("Update title, book title and/or author. Omitted fields are unchanged; the folder name never changes.");
            endpoints.MapPut("/api/projects/{folder}/narrator-only-mode", SetNarratorOnlyModeAsync)
                .RecordsStep("Narrator-only mode", StepAction.Character)
                .WithSummary("Set the Book-wide narrator-only policy: only narration items get audio.");
            endpoints.MapPut("/api/projects/{folder}/cover", SaveCoverAsync)
                .RecordsStep("Change cover", StepAction.Edit)
                .WithSummary("Replace the cover image (multipart 'file': jpg/jpeg/png/webp, at most 10 MB). Served from /workspace/{folder}/{coverImage}.");
            endpoints.MapDelete("/api/projects/{folder}/cover", DeleteCoverAsync)
                .RecordsStep("Remove cover", StepAction.Delete)
                .WithSummary("Remove the cover image; a project without one still answers 204.");
        }

        private static async Task<Results<Ok<IReadOnlyList<ProjectSummary>>, ProblemHttpResult>> ListAsync(
            ProjectUseCases useCases, SeriesSnapshotLoader reads, CancellationToken ct)
        {
            var result = await useCases.GetSummariesAsync();
            if (!result.IsSuccess)
                return TypedResults.Problem(result.Error, statusCode: StatusCodes.Status422UnprocessableEntity);

            var memberships = await reads.MembershipsAsync(ct);
            return TypedResults.Ok<IReadOnlyList<ProjectSummary>>(result.Value!
                .Select(s => s with { Series = memberships.GetValueOrDefault(s.FolderName) })
                .ToList());
        }

        private static async Task<Results<Created<CreateProjectResponse>, ProblemHttpResult>> CreateAsync(HttpRequest request, ProjectUseCases useCases)
        {
            if (!request.HasFormContentType)
                return TypedResults.Problem("Expected multipart form data.", statusCode: StatusCodes.Status400BadRequest);

            var form = await request.ReadFormAsync();
            var title = form["title"].ToString();
            var bookTitle = form["bookTitle"].ToString();
            var author = form["author"].ToString();
            var file = form.Files.GetFile("file");

            if (file is null)
                return TypedResults.Problem(ProjectUseCases.TitleAndFileRequired, statusCode: StatusCodes.Status400BadRequest);

            var fileType = Path.GetExtension(file.FileName).Equals(".epub", StringComparison.OrdinalIgnoreCase)
                ? BookFileType.Epub
                : BookFileType.Text;

            await using var stream = file.OpenReadStream();
            return await useCases.CreateAsync(title, bookTitle, author, file.FileName, stream, fileType) switch
            {
                ProjectOutcome.Created created => TypedResults.Created(
                    $"/api/projects/{created.FolderName}", new CreateProjectResponse(created.FolderName)),
                var other => Problem(other),
            };
        }

        private static async Task<Results<Ok<ProjectDetailDto>, NotFound, ProblemHttpResult>> GetAsync(
            string folder, IFileSystem fs, IProjectCatalogReader reader, ProjectHistory history, SeriesSnapshotLoader reads, CancellationToken ct)
        {
            if (!TryResolve(folder, fs, out var folderId))
                return TypedResults.NotFound();

            // Opening a project is when an existing one meets History: "History started" (ADR 0016).
            await history.StartAsync(folderId, ct);
            return await DetailAsync(folderId, reader, reads, ct);
        }

        private static async Task<Results<NoContent, NotFound, ProblemHttpResult>> DeleteAsync(
            string folder, IFileSystem fs, ProjectUseCases useCases, CancellationToken ct)
        {
            if (!TryResolve(folder, fs, out var folderId))
                return TypedResults.NotFound();

            return await useCases.DeleteAsync(folderId, ct) switch
            {
                ProjectOutcome.Done => TypedResults.NoContent(),
                var other => Problem(other),
            };
        }

        private static async Task<Results<Ok<ProjectDetailDto>, NotFound, ProblemHttpResult>> UpdateAsync(
            string folder, UpdateProjectRequest? body, IFileSystem fs, IProjectCatalogReader reader,
            ProjectUseCases useCases, SeriesSnapshotLoader reads, CancellationToken ct)
        {
            if (!TryResolve(folder, fs, out var folderId))
                return TypedResults.NotFound();
            if (body is null)
                return TypedResults.Problem("Expected a JSON body.", statusCode: StatusCodes.Status400BadRequest);

            return await useCases.UpdateMetadataAsync(folderId, body.Title, body.BookTitle, body.Author) switch
            {
                ProjectOutcome.Done => await DetailAsync(folderId, reader, reads, ct),
                var other => Problem(other),
            };
        }

        private static async Task<Results<NoContent, NotFound, ProblemHttpResult>> SetNarratorOnlyModeAsync(
            string folder, NarratorOnlyModeRequest? body, IFileSystem fs, ProjectUseCases useCases)
        {
            if (!TryResolve(folder, fs, out var folderId))
                return TypedResults.NotFound();
            if (body is null)
                return TypedResults.Problem("Expected a JSON body with 'enabled'.", statusCode: StatusCodes.Status400BadRequest);

            var result = await useCases.SetNarratorOnlyModeAsync(folderId.Value, body.Enabled);
            return result.IsSuccess
                ? TypedResults.NoContent()
                : TypedResults.Problem(result.Error, statusCode: StatusCodes.Status422UnprocessableEntity);
        }

        private static async Task<Results<Ok<CoverImageResponse>, NotFound, ProblemHttpResult>> SaveCoverAsync(
            string folder, HttpRequest request, IFileSystem fs, ProjectUseCases useCases)
        {
            if (!TryResolve(folder, fs, out var folderId))
                return TypedResults.NotFound();

            // The stored name is the client's base name: it is what the detail DTO echoes and what
            // /workspace serves.
            var (upload, refusal) = await UploadedFile.ReadAsync(
                request, ImageUpload.Extensions, ImageUpload.MaxBytes, "Cover image must be 10 MB or smaller.");
            if (upload is null)
                return refusal!;

            await using var stream = upload.File.OpenReadStream();
            var result = await useCases.SaveCoverImageAsync(folderId.Value, upload.FileName, stream);
            return result.IsSuccess
                ? TypedResults.Ok(new CoverImageResponse(upload.FileName))
                : TypedResults.Problem(result.Error, statusCode: StatusCodes.Status422UnprocessableEntity);
        }

        private static async Task<Results<NoContent, NotFound, ProblemHttpResult>> DeleteCoverAsync(string folder, IFileSystem fs, ProjectUseCases useCases)
        {
            if (!TryResolve(folder, fs, out var folderId))
                return TypedResults.NotFound();

            var result = await useCases.DeleteCoverImageAsync(folderId.Value);
            return result.IsSuccess
                ? TypedResults.NoContent()
                : TypedResults.Problem(result.Error, statusCode: StatusCodes.Status422UnprocessableEntity);
        }

        private static async Task<Results<Ok<ProjectDetailDto>, NotFound, ProblemHttpResult>> DetailAsync(
            ProjectFolderId folderId, IProjectCatalogReader reader, SeriesSnapshotLoader reads, CancellationToken ct)
        {
            var project = await reader.GetProjectAsync(folderId);
            if (project is null) return TypedResults.NotFound();

            var narrator = await reader.GetNarratorAsync(folderId, ct);
            return TypedResults.Ok(ProjectDetailDto.From(folderId.Value, project, narrator, await reads.MembershipOfAsync(folderId, ct)));
        }

        private static async Task<Results<NoContent, NotFound, ProblemHttpResult>> ImportAsync(
            string folder, ImportRequest? body, HttpRequest request, IFileSystem fs, BookUseCases useCases,
            MutationOrigin origin, HistoryStepScope steps, CancellationToken ct)
        {
            if (!TryResolve(folder, fs, out var folderId))
                return TypedResults.NotFound();

            // An import that changed nothing is still a legal import, and the wire contract has one
            // success: 204 for a Book that was replaced and for one there was nothing to replace.
            OriginHeader.Apply(request, origin);
            var reread = body?.Reread ?? false;
            await using (steps.OpenImport(folderId, reread ? "Reread book" : "Import book"))
                return ImportResult(await useCases.ImportAsync(folderId, reread, ct));
        }

        private static async Task<Results<NoContent, NotFound, ProblemHttpResult>> ImportManuallyAsync(
            string folder, ManualImportRequest? body, HttpRequest request, IFileSystem fs, BookUseCases useCases,
            MutationOrigin origin, CancellationToken ct)
        {
            if (!TryResolve(folder, fs, out var folderId))
                return TypedResults.NotFound();
            if (body is null)
                return TypedResults.Problem("Expected a JSON body.", statusCode: StatusCodes.Status400BadRequest);
            if (!body.TryToOptions(out var options, out var error))
                return TypedResults.Problem(error, statusCode: StatusCodes.Status400BadRequest);

            OriginHeader.Apply(request, origin);
            return ImportResult(await useCases.ImportManuallyAsync(folderId, options!, ct));
        }

        private static Results<NoContent, NotFound, ProblemHttpResult> ImportResult(BookImportOutcome outcome) =>
            outcome is BookImportOutcome.Failed failed
                ? TypedResults.Problem(failed.Message, statusCode: StatusCodes.Status422UnprocessableEntity)
                : TypedResults.NoContent();

        /// <summary>A lifecycle write that did not happen: refused input is a 400, a failed write a 422.</summary>
        private static ProblemHttpResult Problem(ProjectOutcome outcome) => outcome switch
        {
            ProjectOutcome.Refused refused => TypedResults.Problem(refused.Message, statusCode: StatusCodes.Status400BadRequest),
            ProjectOutcome.Failed failed => TypedResults.Problem(failed.Message, statusCode: StatusCodes.Status422UnprocessableEntity),
            _ => throw new InvalidOperationException($"{outcome} is a success, not a problem."),
        };

        /// Both gates in one place: the name must parse as a single path segment
        /// (traversal guard) and must exist on disk — anything else is a 404.
        internal static bool TryResolve(string folder, IFileSystem fs, out ProjectFolderId folderId) =>
            ProjectFolderId.TryParse(folder, out folderId) && fs.ProjectFolderExists(folderId.Value);
    }
}
