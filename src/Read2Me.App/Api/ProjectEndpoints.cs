using System.Threading;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Read2Me.Core.IO;
using Read2Me.Core.Models;
using Read2Me.Data.Enums;
using Read2Me.Services;
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
                .WithSummary("Read the stored book file into volumes/chapters/paragraphs. reread=true clears existing content first.");
        }

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

        private static async Task<IResult> GetAsync(string folder, IFileSystem fs, IProjectCatalogReader reader)
        {
            if (!TryResolve(folder, fs, out var folderId))
                return Results.NotFound();

            var project = await reader.GetProjectAsync(folderId);
            return project is null
                ? Results.NotFound()
                : Results.Ok(ProjectDetailDto.From(folderId.Value, project));
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

        private static async Task<IResult> ImportAsync(
            string folder, ImportRequest? body, IFileSystem fs, BookUseCases useCases, CancellationToken ct)
        {
            if (!TryResolve(folder, fs, out var folderId))
                return Results.NotFound();

            var result = await useCases.ImportAsync(folderId.Value, body?.Reread ?? false, ct);
            return result.IsSuccess
                ? Results.Ok()
                : Results.Problem(result.Error, statusCode: StatusCodes.Status422UnprocessableEntity);
        }

        /// Both gates in one place: the name must parse as a single path segment
        /// (traversal guard) and must exist on disk — anything else is a 404.
        internal static bool TryResolve(string folder, IFileSystem fs, out ProjectFolderId folderId) =>
            ProjectFolderId.TryParse(folder, out folderId) && fs.ProjectFolderExists(folderId.Value);
    }
}
