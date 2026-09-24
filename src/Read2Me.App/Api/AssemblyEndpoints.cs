using Microsoft.AspNetCore.Builder;
using Read2Me.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Read2Me.Core.IO;
using Read2Me.Services;
using Read2Me.Services.Audio;
using Read2Me.Services.Audio.Assembly;

namespace Read2Me.App.Api
{
    public sealed record AssemblyStartRequest(bool AllowPartial = false);

    /// <summary>
    /// <c>Folder</c> is the project of the running (or most recent) job; <c>OutputFileName</c> is set
    /// once that job has completed and names a file under its assembly outputs.
    /// </summary>
    public sealed record AssemblyStatusDto(
        bool IsRunning, string? CurrentPhase, double EncodePercent,
        string? LastError, int AudioRemainingCount,
        string? Folder, string? OutputFileName);

    public sealed record AssemblyOutputDto(string FileName, long SizeBytes, DateTimeOffset CreatedAt, bool IsPartial);

    public static class AssemblyEndpoints
    {
        public static void MapAssemblyEndpoints(this IEndpointRouteBuilder endpoints)
        {
            endpoints.MapPost("/api/projects/{folder}/assembly", StartAsync)
                .WithSummary("Start m4b assembly. 409 with audioRemainingCount when items still lack audio and allowPartial is false. Poll /api/assembly/status.");
            endpoints.MapGet("/api/assembly/status",
                    (AudiobookAssemblyService svc) => Results.Ok(new AssemblyStatusDto(
                        svc.IsRunning, svc.CurrentPhase?.ToString(), svc.EncodePercent,
                        svc.LastError, svc.AudioRemainingCount, svc.Folder, svc.OutputFileName)))
                .WithSummary("Assembly progress: phase (Gather/Silence/ProbeConcat/Encode/Finalize) and encode percent, the folder of the running or most recent job, and outputFileName once it completed.");
            endpoints.MapPost("/api/assembly/cancel",
                    (AudiobookAssemblyService svc) => { svc.Cancel(); return Results.Ok(); })
                .WithSummary("Cancel the running assembly.");

            endpoints.MapGet("/api/projects/{folder}/assembly/outputs", ListOutputs)
                .WithSummary("Assembled audiobooks under the project's output folder, newest first. isPartial marks a build that skipped items without audio.");
            endpoints.MapGet("/api/projects/{folder}/assembly/outputs/{fileName}", DownloadOutput)
                .WithSummary("Download one assembled audiobook (audio/mp4 attachment; Range requests supported).");
            endpoints.MapDelete("/api/projects/{folder}/assembly/outputs/{fileName}", DeleteOutput)
                .WithSummary("Delete one assembled audiobook (204; 404 when it is not an existing output).");
        }

        private static IResult ListOutputs(string folder, IFileSystem fs)
        {
            if (!ProjectEndpoints.TryResolve(folder, fs, out var folderId))
                return Results.NotFound();

            return Results.Ok(AssemblyOutputs.List(fs.GetProjectFolderPath(folderId.Value))
                .Select(o => new AssemblyOutputDto(o.FileName, o.SizeBytes, o.CreatedAt, o.IsPartial)));
        }

        private static IResult DownloadOutput(string folder, string fileName, IFileSystem fs)
        {
            if (!TryResolveOutput(folder, fileName, fs, out var path))
                return Results.NotFound();

            return Results.File(path, "audio/mp4", fileDownloadName: fileName, enableRangeProcessing: true);
        }

        private static IResult DeleteOutput(string folder, string fileName, IFileSystem fs)
        {
            if (!TryResolveOutput(folder, fileName, fs, out var path))
                return Results.NotFound();

            fs.DeleteFile(path);
            return Results.NoContent();
        }

        /// Folder and file name are both vetted before either reaches a path, so a traversal
        /// attempt is a 404 rather than a read (the same stance as the /workspace mount).
        private static bool TryResolveOutput(string folder, string fileName, IFileSystem fs, out string path)
        {
            path = string.Empty;
            if (!ProjectEndpoints.TryResolve(folder, fs, out var folderId) ||
                !AssemblyOutputs.TryResolve(fs.GetProjectFolderPath(folderId.Value), fileName, out var resolved))
                return false;

            path = resolved;
            return true;
        }

        private static async Task<IResult> StartAsync(
            string folder, AssemblyStartRequest? body, IFileSystem fs,
            IAudioItemReader reader, AudiobookAssemblyService service, CancellationToken ct)
        {
            if (!ProjectEndpoints.TryResolve(folder, fs, out var folderId))
                return Results.NotFound();

            var allowPartial = body?.AllowPartial ?? false;

            // The service only discovers missing audio after its async Gather phase; an
            // agent needs the answer in the response, so run the same count up front.
            if (!allowPartial)
            {
                var manifest = await reader.GetAssemblyManifestAsync(folderId, ct);
                var remaining = manifest.Count(e =>
                    !ParagraphItemKinds.IsPause(e.ItemType) && e.AudioRelativePath == null);
                if (remaining > 0)
                    return Results.Problem(
                        $"{remaining} items still need audio. Generate audio first or pass allowPartial.",
                        statusCode: StatusCodes.Status409Conflict,
                        extensions: new Dictionary<string, object?> { ["audioRemainingCount"] = remaining });
            }

            return service.StartAsync(folderId, allowPartial)
                ? Results.Accepted(value: new { started = true })
                : Results.Problem("Assembly is already running.", statusCode: StatusCodes.Status409Conflict);
        }
    }
}
