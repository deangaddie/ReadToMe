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
    public sealed record AssemblyStatusDto(
        bool IsRunning, string? CurrentPhase, double EncodePercent,
        string? LastError, int AudioRemainingCount);

    public static class AssemblyEndpoints
    {
        public static void MapAssemblyEndpoints(this IEndpointRouteBuilder endpoints)
        {
            endpoints.MapPost("/api/projects/{folder}/assembly", StartAsync)
                .WithSummary("Start m4b assembly. 409 with audioRemainingCount when items still lack audio and allowPartial is false. Poll /api/assembly/status.");
            endpoints.MapGet("/api/assembly/status",
                    (AudiobookAssemblyService svc) => Results.Ok(new AssemblyStatusDto(
                        svc.IsRunning, svc.CurrentPhase?.ToString(), svc.EncodePercent,
                        svc.LastError, svc.AudioRemainingCount)))
                .WithSummary("Assembly progress: phase (Gather/Silence/ProbeConcat/Encode/Finalize) and encode percent.");
            endpoints.MapPost("/api/assembly/cancel",
                    (AudiobookAssemblyService svc) => { svc.Cancel(); return Results.Ok(); })
                .WithSummary("Cancel the running assembly.");
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
