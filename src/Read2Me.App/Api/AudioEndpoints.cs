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
    /// <summary>An explicit item selection or a single retry. Ids that name no item are ignored.</summary>
    public sealed record ItemsEnqueueRequest(Guid[] ItemIds);
    public sealed record AudioItemStatusDto(string? Status, AudioItemOutcome? Outcome, long? AudioVersion);

    /// <summary><see cref="AudioReviewInfo"/> on the wire, with the state as its member name.</summary>
    public sealed record AudioReviewDto(
        string State, bool NormalizeOk, string? NormalizeReason, bool VerifyOk, double? Wer,
        string? VerifyReason, string? Transcript, string? OriginalTextSnapshot);

    public static class AudioEndpoints
    {
        public static void MapAudioEndpoints(this IEndpointRouteBuilder endpoints)
        {
            endpoints.MapPost("/api/projects/{folder}/audio/enqueue", EnqueueAsync)
                .WithSummary("Queue TTS audio generation for the items under a node (level: volume|part|chapter). Poll /api/audio/queue.");
            endpoints.MapPost("/api/projects/{folder}/audio/enqueue-items", EnqueueItemsAsync)
                .WithSummary("Queue TTS audio generation for an explicit list of item ids (a selection, or one item to retry). Unknown ids are ignored; the count answers with what was queued. 409 when no paragraph TTS service is active.");
            endpoints.MapGet("/api/projects/{folder}/audio/items/{itemId:guid}", GetItemStatus)
                .WithSummary("Per-item audio state: queued/processing status, failure outcome, or the audio version stamp once complete.");
            endpoints.MapGet("/api/projects/{folder}/audio/reviews", GetReviewsAsync)
                .WithSummary("Items whose audio needs review or had it dismissed: { itemId: { state, normalizeOk, normalizeReason, verifyOk, wer, verifyReason, transcript, originalTextSnapshot } }. Sparse: an item with no entry passed both checks.");
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

            if (await NoActiveTtsAsync(ttsSettings) is { } refused)
                return refused;

            var enqueued = await useCases.EnqueueAudioAsync(
                folderId, level, request.NodeId, request.NeedsAudioOnly, request.NarratorOnlyMode);
            return Results.Accepted(value: new EnqueueResponse(enqueued));
        }

        private static async Task<IResult> EnqueueItemsAsync(
            string folder, ItemsEnqueueRequest request, IFileSystem fs,
            EnqueueUseCases useCases, ParagraphTtsSettingsService ttsSettings)
        {
            if (!ProjectEndpoints.TryResolve(folder, fs, out var folderId))
                return Results.NotFound();

            if (await NoActiveTtsAsync(ttsSettings) is { } refused)
                return refused;

            var enqueued = await useCases.EnqueueAudioAsync(folderId, request.ItemIds ?? []);
            return Results.Accepted(value: new EnqueueResponse(enqueued));
        }

        /// <summary>The 409 every enqueue answers when no paragraph TTS service is active; null when one is.</summary>
        private static async Task<IResult?> NoActiveTtsAsync(ParagraphTtsSettingsService ttsSettings) =>
            await ttsSettings.GetActiveConfigAsync() is null
                ? Results.Problem("No paragraph TTS service configured. Create one via /api/settings/paragraph-tts.",
                    statusCode: StatusCodes.Status409Conflict)
                : null;

        private static async Task<IResult> GetReviewsAsync(string folder, IFileSystem fs, IAudioItemReader reader)
        {
            if (!ProjectEndpoints.TryResolve(folder, fs, out var folderId))
                return Results.NotFound();

            var rows = await reader.GetAudioReviewsAsync(folderId);
            return Results.Ok(rows.ToDictionary(
                r => r.ParagraphItemId.ToString(),
                r => new AudioReviewDto(
                    r.Info.State.ToString(), r.Info.NormalizeOk, r.Info.NormalizeReason, r.Info.VerifyOk,
                    r.Info.Wer, r.Info.VerifyReason, r.Info.Transcript, r.Info.OriginalTextSnapshot)));
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
