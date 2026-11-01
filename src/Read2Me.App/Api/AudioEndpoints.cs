using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Read2Me.Core.IO;
using Read2Me.Core.Models;
using Read2Me.Services;
using Read2Me.Services.Audio;
using Read2Me.Services.UseCases;

namespace Read2Me.App.Api
{
    public sealed record AudioEnqueueRequest(
        BookNodeLevel Level, Guid NodeId, bool NeedsAudioOnly = true, bool NarratorOnlyMode = false);
    /// <summary>An explicit item selection or a single retry. Ids that name no item are ignored.</summary>
    public sealed record ItemsEnqueueRequest(Guid[] ItemIds);
    public sealed record AudioItemStatusDto(AudioItemQueueStatus? Status, AudioItemOutcome? Outcome, long? AudioVersion);

    /// <summary><see cref="AudioReviewInfo"/> on the wire, with nulls sent explicitly.</summary>
    public sealed record AudioReviewDto(
        AudioReviewState State, bool NormalizeOk, string? NormalizeReason, bool VerifyOk, double? Wer,
        string? VerifyReason, string? Transcript, string? OriginalTextSnapshot);

    public static class AudioEndpoints
    {
        public static void MapAudioEndpoints(this IEndpointRouteBuilder endpoints)
        {
            endpoints.MapPost("/api/projects/{folder}/audio/enqueue", EnqueueAsync)
                .RecordsNoStep("Enqueues a Background run; takes are results.")
                .WithSummary("Queue TTS audio generation for the items under a node (level: Volume|Part|Chapter). Poll /api/audio/queue.");
            endpoints.MapPost("/api/projects/{folder}/audio/enqueue-items", EnqueueItemsAsync)
                .RecordsNoStep("Enqueues a Background run; takes are results.")
                .WithSummary("Queue TTS audio generation for an explicit list of item ids (a selection, or one item to retry). Unknown ids are ignored; the count answers with what was queued. 409 when no paragraph TTS service is active.");
            endpoints.MapGet("/api/projects/{folder}/audio/items/{itemId:guid}", GetItemStatus)
                .ReadsLive("an item's place in the Audio Queue is live progress.")
                .WithSummary("Per-item audio state: queued/processing status, failure outcome, or the audio version stamp once complete.");
            endpoints.MapGet("/api/projects/{folder}/audio/reviews", GetReviewsAsync)
                .ReadsLive("audio reviews are results, which History doesn't record.")
                .WithSummary("Items whose audio needs review or had it dismissed: { itemId: { state, normalizeOk, normalizeReason, verifyOk, wer, verifyReason, transcript, originalTextSnapshot } }. Sparse: an item with no entry passed both checks.");
            endpoints.MapPost("/api/audio/cancel",
                    (AudioQueueService queue) => { queue.CancelAll(); return TypedResults.NoContent(); })
                .WithSummary("Cancel all queued audio work. 204, also when nothing is queued.");
        }

        private static async Task<Results<Accepted<EnqueueResponse>, NotFound, ProblemHttpResult>> EnqueueAsync(
            string folder, AudioEnqueueRequest request, IFileSystem fs,
            EnqueueUseCases useCases, ParagraphTtsSettingsService ttsSettings)
        {
            if (!ProjectEndpoints.TryResolve(folder, fs, out var folderId))
                return TypedResults.NotFound();
            if (await NoActiveTtsAsync(ttsSettings) is { } refused)
                return refused;

            var enqueued = await useCases.EnqueueAudioAsync(
                folderId, request.Level, request.NodeId, request.NeedsAudioOnly, request.NarratorOnlyMode);
            return EnqueueResponse.For(enqueued);
        }

        private static async Task<Results<Accepted<EnqueueResponse>, NotFound, ProblemHttpResult>> EnqueueItemsAsync(
            string folder, ItemsEnqueueRequest request, IFileSystem fs,
            EnqueueUseCases useCases, ParagraphTtsSettingsService ttsSettings)
        {
            if (!ProjectEndpoints.TryResolve(folder, fs, out var folderId))
                return TypedResults.NotFound();

            if (await NoActiveTtsAsync(ttsSettings) is { } refused)
                return refused;

            var enqueued = await useCases.EnqueueAudioAsync(folderId, request.ItemIds ?? []);
            return EnqueueResponse.For(enqueued);
        }

        /// <summary>The 409 every enqueue answers when no paragraph TTS service is active; null when one is.</summary>
        private static async Task<ProblemHttpResult?> NoActiveTtsAsync(ParagraphTtsSettingsService ttsSettings) =>
            await ttsSettings.GetActiveConfigAsync() is null
                ? TypedResults.Problem("No paragraph TTS service configured. Create one via /api/settings/paragraph-tts.",
                    statusCode: StatusCodes.Status409Conflict)
                : null;

        private static async Task<Results<Ok<Dictionary<string, AudioReviewDto>>, NotFound>> GetReviewsAsync(string folder, IFileSystem fs, IAudioItemReader reader)
        {
            if (!ProjectEndpoints.TryResolve(folder, fs, out var folderId))
                return TypedResults.NotFound();

            var rows = await reader.GetAudioReviewsAsync(folderId);
            return TypedResults.Ok(rows.ToDictionary(
                r => r.ParagraphItemId.ToString(),
                r => new AudioReviewDto(
                    r.Info.State, r.Info.NormalizeOk, r.Info.NormalizeReason, r.Info.VerifyOk,
                    r.Info.Wer, r.Info.VerifyReason, r.Info.Transcript, r.Info.OriginalTextSnapshot)));
        }

        private static Results<Ok<AudioItemStatusDto>, NotFound> GetItemStatus(
            string folder, Guid itemId, IFileSystem fs, AudioQueueService queue)
        {
            if (!ProjectEndpoints.TryResolve(folder, fs, out var folderId))
                return TypedResults.NotFound();

            return TypedResults.Ok(new AudioItemStatusDto(
                queue.StatusOf(folderId, itemId),
                queue.OutcomeOf(folderId, itemId),
                queue.AudioVersionOf(folderId, itemId)));
        }
    }
}
