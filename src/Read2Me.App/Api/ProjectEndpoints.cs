using System.Threading;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Read2Me.Core.IO;
using Read2Me.Core.Models;
using Read2Me.Data;
using Read2Me.Data.Enums;
using Read2Me.Services;
using Read2Me.Services.Mutations;
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
                .WithSummary("Project metadata for one folder.");
            endpoints.MapDelete("/api/projects/{folder}", Delete)
                .WithSummary("Delete a project folder and everything in it.");
            endpoints.MapPost("/api/projects/{folder}/import", ImportAsync)
                .WithSummary("Read the stored book file into volumes/chapters/paragraphs. reread=true clears existing content first. " +
                             "An X-Origin-Id header (GUID) is echoed as originId on the mutation receipt the live hub publishes.");
            endpoints.MapPost("/api/projects/{folder}/import/manual", ImportManuallyAsync)
                .WithSummary("Re-split the stored book file by hand-chosen rules (replaces existing content). " +
                             "Per level: mode Prefix (with prefix) | Arabic | Roman. 400 when a switched-on level lacks a valid rule. " +
                             "An X-Origin-Id header (GUID) is echoed as originId on the mutation receipt the live hub publishes.");
            endpoints.MapPatch("/api/projects/{folder}", UpdateAsync)
                .WithSummary("Update title, book title and/or author. Omitted fields are unchanged; the folder name never changes.");
            endpoints.MapPut("/api/projects/{folder}/narrator-only-mode", SetNarratorOnlyModeAsync)
                .WithSummary("Set the Book-wide narrator-only policy: only narration items get audio.");
            endpoints.MapPut("/api/projects/{folder}/cover", SaveCoverAsync)
                .WithSummary("Replace the cover image (multipart 'file': jpg/jpeg/png/webp, at most 10 MB). Served from /workspace/{folder}/{coverImage}.");
            endpoints.MapDelete("/api/projects/{folder}/cover", DeleteCoverAsync)
                .WithSummary("Remove the cover image; a project without one still answers 204.");
        }

        private static readonly string[] CoverExtensions = [".jpg", ".jpeg", ".png", ".webp"];
        private const long MaxCoverBytes = 10L * 1024 * 1024;

        private static async Task<IResult> ListAsync(ProjectUseCases useCases)
        {
            var result = await useCases.GetSummariesAsync();
            return result.IsSuccess
                ? Results.Ok(result.Value)
                : Results.Problem(result.Error, statusCode: StatusCodes.Status422UnprocessableEntity);
        }

        private static async Task<IResult> CreateAsync(HttpRequest request, ProjectUseCases useCases)
        {
            if (!request.HasFormContentType)
                return Results.Problem("Expected multipart form data.", statusCode: StatusCodes.Status400BadRequest);

            var form = await request.ReadFormAsync();
            var title = form["title"].ToString();
            var bookTitle = form["bookTitle"].ToString();
            var author = form["author"].ToString();
            var file = form.Files.GetFile("file");

            if (string.IsNullOrWhiteSpace(title) || file is null)
                return Results.Problem("Fields 'title' and 'file' are required.", statusCode: StatusCodes.Status400BadRequest);

            var fileType = Path.GetExtension(file.FileName).Equals(".epub", StringComparison.OrdinalIgnoreCase)
                ? BookFileType.Epub
                : BookFileType.Text;

            await using var stream = file.OpenReadStream();
            var result = await useCases.CreateAsync(title, bookTitle, author, file.FileName, stream, fileType);
            return result.IsSuccess
                ? Results.Created($"/api/projects/{result.Value}", new CreateProjectResponse(result.Value))
                : Results.Problem(result.Error, statusCode: StatusCodes.Status422UnprocessableEntity);
        }

        private static async Task<IResult> GetAsync(
            string folder, IFileSystem fs, IProjectCatalogReader reader, CancellationToken ct)
        {
            if (!TryResolve(folder, fs, out var folderId))
                return Results.NotFound();

            return await DetailAsync(folderId, reader, ct);
        }

        private static IResult Delete(string folder, IFileSystem fs, ProjectUseCases useCases)
        {
            if (!TryResolve(folder, fs, out var folderId))
                return Results.NotFound();

            var result = useCases.DeleteProject(folderId.Value);
            return result.IsSuccess
                ? Results.NoContent()
                : Results.Problem(result.Error, statusCode: StatusCodes.Status422UnprocessableEntity);
        }

        private static async Task<IResult> UpdateAsync(
            string folder, UpdateProjectRequest? body, IFileSystem fs, IProjectCatalogReader reader,
            ProjectUseCases useCases, CancellationToken ct)
        {
            if (!TryResolve(folder, fs, out var folderId))
                return Results.NotFound();
            if (body is null)
                return Results.Problem("Expected a JSON body.", statusCode: StatusCodes.Status400BadRequest);

            // A provided-but-blank title or book title is a mistake, not a clear; author may be blank.
            var title = body.Title?.Trim();
            var bookTitle = body.BookTitle?.Trim();
            var author = body.Author?.Trim();
            if (title is not null && title.Length == 0)
                return Results.Problem("Title cannot be blank.", statusCode: StatusCodes.Status400BadRequest);
            if (bookTitle is not null && bookTitle.Length == 0)
                return Results.Problem("Book title cannot be blank.", statusCode: StatusCodes.Status400BadRequest);

            var result = await useCases.UpdateMetadataAsync(folderId.Value, title, bookTitle, author);
            if (!result.IsSuccess)
                return Results.Problem(result.Error, statusCode: StatusCodes.Status422UnprocessableEntity);

            return await DetailAsync(folderId, reader, ct);
        }

        private static async Task<IResult> SetNarratorOnlyModeAsync(
            string folder, NarratorOnlyModeRequest? body, IFileSystem fs, ProjectUseCases useCases)
        {
            if (!TryResolve(folder, fs, out var folderId))
                return Results.NotFound();
            if (body is null)
                return Results.Problem("Expected a JSON body with 'enabled'.", statusCode: StatusCodes.Status400BadRequest);

            var result = await useCases.SetNarratorOnlyModeAsync(folderId.Value, body.Enabled);
            return result.IsSuccess
                ? Results.NoContent()
                : Results.Problem(result.Error, statusCode: StatusCodes.Status422UnprocessableEntity);
        }

        private static async Task<IResult> SaveCoverAsync(
            string folder, HttpRequest request, IFileSystem fs, ProjectUseCases useCases)
        {
            if (!TryResolve(folder, fs, out var folderId))
                return Results.NotFound();
            if (!request.HasFormContentType)
                return Results.Problem("Expected multipart form data.", statusCode: StatusCodes.Status400BadRequest);

            IFormFile? file;
            try
            {
                file = (await request.ReadFormAsync()).Files.GetFile("file");
            }
            catch (InvalidDataException ex)
            {
                // A malformed multipart body (no boundary parts, truncated) is the client's error.
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest);
            }
            if (file is null)
                return Results.Problem("Field 'file' is required.", statusCode: StatusCodes.Status400BadRequest);

            // The stored name is the client's base name: it is what the detail DTO echoes and what
            // /workspace serves, so a path in it would be both a traversal and a broken link.
            var fileName = Path.GetFileName(file.FileName);
            var extension = Path.GetExtension(fileName).ToLowerInvariant();
            if (!CoverExtensions.Contains(extension))
                return Results.Problem("Unsupported format. Use .jpg, .png, or .webp.", statusCode: StatusCodes.Status400BadRequest);
            if (file.Length > MaxCoverBytes)
                return Results.Problem("Cover image must be 10 MB or smaller.", statusCode: StatusCodes.Status400BadRequest);

            await using var stream = file.OpenReadStream();
            var result = await useCases.SaveCoverImageAsync(folderId.Value, fileName, stream);
            return result.IsSuccess
                ? Results.Ok(new CoverImageResponse(fileName))
                : Results.Problem(result.Error, statusCode: StatusCodes.Status422UnprocessableEntity);
        }

        private static async Task<IResult> DeleteCoverAsync(string folder, IFileSystem fs, ProjectUseCases useCases)
        {
            if (!TryResolve(folder, fs, out var folderId))
                return Results.NotFound();

            var result = await useCases.DeleteCoverImageAsync(folderId.Value);
            return result.IsSuccess
                ? Results.NoContent()
                : Results.Problem(result.Error, statusCode: StatusCodes.Status422UnprocessableEntity);
        }

        private static async Task<IResult> DetailAsync(ProjectFolderId folderId, IProjectCatalogReader reader, CancellationToken ct)
        {
            var project = await reader.GetProjectAsync(folderId);
            if (project is null) return Results.NotFound();

            var narrator = await reader.GetNarratorAsync(folderId, ct);
            return Results.Ok(ProjectDetailDto.From(folderId.Value, project, narrator));
        }

        private static async Task<IResult> ImportAsync(
            string folder, ImportRequest? body, HttpRequest request, IFileSystem fs, BookUseCases useCases,
            MutationOrigin origin, CancellationToken ct)
        {
            if (!TryResolve(folder, fs, out var folderId))
                return Results.NotFound();

            // An import that changed nothing is still a legal import, and the wire contract has one
            // success: 200 for a Book that was replaced and for one there was nothing to replace.
            OriginHeader.Apply(request, origin);
            return ImportResult(await useCases.ImportAsync(folderId, body?.Reread ?? false, ct));
        }

        private static async Task<IResult> ImportManuallyAsync(
            string folder, ManualImportRequest? body, HttpRequest request, IFileSystem fs, BookUseCases useCases,
            MutationOrigin origin, CancellationToken ct)
        {
            if (!TryResolve(folder, fs, out var folderId))
                return Results.NotFound();
            if (body is null)
                return Results.Problem("Expected a JSON body.", statusCode: StatusCodes.Status400BadRequest);
            if (!body.TryToOptions(out var options, out var error))
                return Results.Problem(error, statusCode: StatusCodes.Status400BadRequest);

            OriginHeader.Apply(request, origin);
            return ImportResult(await useCases.ImportManuallyAsync(folderId, options!, ct));
        }

        private static IResult ImportResult(BookImportOutcome outcome) =>
            outcome is BookImportOutcome.Failed failed
                ? Results.Problem(failed.Message, statusCode: StatusCodes.Status422UnprocessableEntity)
                : Results.Ok();

        /// Both gates in one place: the name must parse as a single path segment
        /// (traversal guard) and must exist on disk — anything else is a 404.
        internal static bool TryResolve(string folder, IFileSystem fs, out ProjectFolderId folderId) =>
            ProjectFolderId.TryParse(folder, out folderId) && fs.ProjectFolderExists(folderId.Value);
    }
}
