using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Read2Me.Core.IO;
using Read2Me.Core.Models;
using Read2Me.Services;
using Read2Me.Services.Audio;
using Read2Me.Services.UseCases;

namespace Read2Me.App.Api
{
    public sealed record AudioEnqueueRequest(
        string Level, Guid NodeId, bool NeedsAudioOnly = true, bool NarratorOnlyMode = false);
    public sealed record AudioItemStatusDto(string? Status, AudioItemOutcome? Outcome, long? AudioVersion);

    public static class AudioEndpoints
    {
        public static void MapAudioEndpoints(this IEndpointRouteBuilder endpoints)
        {
            endpoints.MapPost("/api/projects/{folder}/audio/enqueue", EnqueueAsync)
                .WithSummary("Queue TTS audio generation for the items under a node (level: volume|part|chapter). Poll /api/audio/queue.");
            endpoints.MapGet("/api/projects/{folder}/audio/items/{itemId:guid}", GetItemStatus)
                .WithSummary("Per-item audio state: queued/processing status, failure outcome, or the audio version stamp once complete.");
            endpoints.MapPost("/api/audio/cancel",
                    (AudioQueueService queue) => { queue.CancelAll(); return Results.Ok(); })
                .WithSummary("Cancel all queued audio work.");
        }

        private static async Task<IResult> EnqueueAsync(
            string folder, AudioEnqueueRequest request, IFileSystem fs,
            EnqueueUseCases useCases, ParagraphTtsSettingsService ttsSettings)
        {
            if (!ProjectEndpoints.TryResolve(folder, fs, out var folderId))
                return Results.NotFound();
            if (!Enum.TryParse<BookNodeLevel>(request.Level, ignoreCase: true, out var level))
                return Results.Problem($"Unknown level '{request.Level}'. Expected volume, part or chapter.",
                    statusCode: StatusCodes.Status400BadRequest);

            if (await ttsSettings.GetActiveConfigAsync() is null)
                return Results.Problem("No paragraph TTS service configured. Create one via /api/settings/paragraph-tts.",
                    statusCode: StatusCodes.Status409Conflict);

            var enqueued = await useCases.EnqueueAudioAsync(
                folderId, level, request.NodeId, request.NeedsAudioOnly, request.NarratorOnlyMode);
            return Results.Accepted(value: new EnqueueResponse(enqueued));
        }

        private static IResult GetItemStatus(
            string folder, Guid itemId, IFileSystem fs, AudioQueueService queue)
        {
            if (!ProjectEndpoints.TryResolve(folder, fs, out var folderId))
                return Results.NotFound();

            return Results.Ok(new AudioItemStatusDto(
                queue.StatusOf(folderId, itemId)?.ToString(),
                queue.OutcomeOf(folderId, itemId),
                queue.AudioVersionOf(folderId, itemId)));
        }
    }
}
