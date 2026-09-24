using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Read2Me.Core.IO;
using Read2Me.Services;
using Read2Me.Services.Audio;

namespace Read2Me.App.Api
{
    public sealed record AudioProcessingUpdateRequest(
        string? FfmpegPath = null,
        double? WerThreshold = null,
        int? AudioMaxAttempts = null,
        int? ChunkPauseMs = null);

    /// <summary>The assembler's per-kind silences, as one object so a card saves them together.</summary>
    public sealed record PauseDurationsDto(int VolumeMs, int PartMs, int ChapterMs, int ParagraphMs, int PauseMs);

    /// <summary>Everything the audio-processing settings page shows, in one read.</summary>
    public sealed record AudioProcessingFullResponse(
        string? FfmpegPath,
        double WerThreshold,
        bool SentenceSplitEnabled,
        int ChunkPauseMs,
        int AudioMaxAttempts,
        PauseDurationsDto Pauses,
        IReadOnlyList<AudioPostProcessStepConfig> Steps);

    public sealed record FfmpegTestRequest(string? FfmpegPath = null);

    public sealed record PreviewSampleRef(string? Folder, Guid ItemId);
    public sealed record StepPreviewRequest(PreviewSampleRef? Sample, JsonElement? Settings);

    /// <summary>
    /// One rendered A/B preview. <see cref="AppliedOk"/> false means the step fell back, so the
    /// processed URL plays the unprocessed audio and <see cref="Reason"/> says why.
    /// <see cref="RemovedMs"/> is set only after a successful render — the trim card reads it.
    /// </summary>
    public sealed record StepPreviewResponse(
        string PreviewId, string OriginalUrl, string ProcessedUrl, double? RemovedMs, string? Reason, bool AppliedOk);

    public sealed record RecentAudioSampleDto(
        Guid ItemId, string Folder, string Text, string? CharacterName, string? VoiceName, string ProjectTitle);

    /// <summary>
    /// The audio post-processing settings row on the wire (Angular ticket 24): scalars, pause
    /// durations, the paragraph step configs, the ffmpeg probe, the recent-sample picker and the
    /// one-step A/B preview. Previews park in the process-wide <see cref="AudioPreviewStore"/> under
    /// a minted id and play back through the same <c>/audio-preview</c> and <c>/preview-source</c>
    /// routes Blazor's cards use.
    /// </summary>
    public static class AudioProcessingEndpoints
    {
        private const int DefaultSampleLimit = 20;
        private const int MaxSampleLimit = 50;

        public static void MapAudioProcessingEndpoints(this IEndpointRouteBuilder endpoints)
        {
            endpoints.MapGet("/api/settings/audio-processing", GetAudioProcessingAsync)
                .WithSummary("Audio post-processing scalars: ffmpeg path, WER threshold, retry count, pause durations.");
            endpoints.MapPut("/api/settings/audio-processing", UpdateAudioProcessingAsync)
                .WithSummary("Update audio post-processing scalars; only supplied fields change.");
            endpoints.MapGet("/api/settings/audio-processing/full", GetFullAsync)
                .WithSummary("The scalars plus the assembler pause durations and the paragraph post-process step configs (silence-trim, consonant-soften), in pipeline order.");
            endpoints.MapPut("/api/settings/audio-processing/pauses", SetPausesAsync)
                .WithSummary("Save the five assembler pause durations together. 400 on a negative value.");
            endpoints.MapPut("/api/settings/audio-processing/steps/{stepId}", UpsertStepAsync)
                .WithSummary("Save one paragraph post-process step's enabled flag and settings; the other step keeps its config. 400 for a step outside the paragraph pipeline or a body whose stepId differs from the route.");
            endpoints.MapPost("/api/settings/audio-processing/ffmpeg/test", TestFfmpegAsync)
                .WithSummary("Persist the given ffmpeg path (blank = rely on PATH), then probe it. Answers { success, message }.");
            endpoints.MapPost("/api/settings/audio-processing/steps/{stepId}/preview", PreviewStepAsync)
                .WithSummary("Render one step with unsaved settings over a recent sample's Preview Source. Answers the unprocessed and processed WAV URLs; appliedOk false means the step fell back and reason says why. 404 unknown folder, 422 when the sample's preview source has been evicted.");
            endpoints.MapGet("/api/audio/samples/recent", GetRecentSamplesAsync)
                .WithSummary("The most recently generated paragraph items that still hold a Preview Source, newest first (?limit=20, max 50). The A/B preview picker's rows.");
        }

        private static async Task<IResult> GetAudioProcessingAsync(AudioProcessingSettingsService svc) =>
            Results.Ok(await svc.GetAsync());

        private static async Task<IResult> UpdateAudioProcessingAsync(
            AudioProcessingUpdateRequest request, AudioProcessingSettingsService svc)
        {
            if (request.FfmpegPath is not null)
                await svc.SetFfmpegPathAsync(request.FfmpegPath);
            if (request.WerThreshold is { } wer)
                await svc.SetWerThresholdAsync(wer);
            if (request.AudioMaxAttempts is { } attempts)
                await svc.SetAudioMaxAttemptsAsync(attempts);
            if (request.ChunkPauseMs is { } chunk)
                await svc.SetChunkPauseAsync(chunk);
            return Results.Ok(await svc.GetAsync());
        }

        private static async Task<IResult> GetFullAsync(AudioProcessingSettingsService svc) =>
            Results.Ok(await FullAsync(svc));

        private static async Task<AudioProcessingFullResponse> FullAsync(AudioProcessingSettingsService svc)
        {
            var s = await svc.GetAsync();
            var steps = await svc.GetPostProcessStepsAsync();
            return new AudioProcessingFullResponse(
                s.FfmpegPath, s.WerThreshold, s.SentenceSplitEnabled, s.ChunkPauseMs, s.AudioMaxAttempts,
                new PauseDurationsDto(s.VolumePauseMs, s.PartPauseMs, s.ChapterPauseMs, s.ParagraphPauseMs, s.PauseMs),
                steps);
        }

        private static async Task<IResult> SetPausesAsync(PauseDurationsDto request, AudioProcessingSettingsService svc)
        {
            if (request.VolumeMs < 0 || request.PartMs < 0 || request.ChapterMs < 0 || request.ParagraphMs < 0 || request.PauseMs < 0)
                return Results.Problem("Pause durations must be zero or greater.", statusCode: StatusCodes.Status400BadRequest);

            await svc.SetPauseDurationsAsync(request.VolumeMs, request.PartMs, request.ChapterMs, request.ParagraphMs, request.PauseMs);
            return Results.Ok(request);
        }

        private static async Task<IResult> UpsertStepAsync(
            string stepId, AudioPostProcessStepConfig? request, AudioProcessingSettingsService svc)
        {
            if (UnknownStep(stepId) is { } refusal)
                return refusal;
            if (request is null)
                return Results.Problem("Missing step config body.", statusCode: StatusCodes.Status400BadRequest);
            if (!string.Equals(request.StepId, stepId, StringComparison.Ordinal))
                return Results.Problem($"Body stepId '{request.StepId}' does not match the route's '{stepId}'.",
                    statusCode: StatusCodes.Status400BadRequest);

            await svc.UpsertPostProcessStepAsync(request);
            var saved = (await svc.GetPostProcessStepsAsync()).First(s => s.StepId == stepId);
            return Results.Ok(saved);
        }

        private static async Task<IResult> TestFfmpegAsync(
            FfmpegTestRequest? request, AudioProcessingSettingsService svc, CancellationToken ct)
        {
            // Probe the path the user typed, persisting it first so the probe matches the field —
            // the same order as Blazor's Test button.
            if (request?.FfmpegPath is not null)
                await svc.SetFfmpegPathAsync(request.FfmpegPath);
            return Results.Ok(await svc.TestFfmpegAsync(ct));
        }

        private static async Task<IResult> PreviewStepAsync(
            string stepId, StepPreviewRequest? request, IFileSystem fs,
            IAudioPostProcessPreviewRenderer renderer, CancellationToken ct)
        {
            if (UnknownStep(stepId) is { } refusal)
                return refusal;
            if (request?.Sample?.Folder is null)
                return Results.Problem("sample { folder, itemId } is required.", statusCode: StatusCodes.Status400BadRequest);
            if (!ProjectEndpoints.TryResolve(request.Sample.Folder, fs, out var folderId))
                return Results.NotFound();

            // The draft's enabled flag is ignored by the renderer: auditioning an off step is the point.
            var draft = new AudioPostProcessStepConfig(stepId, true, request.Settings);
            var previewId = Guid.NewGuid().ToString("N");
            var result = await renderer.RenderAsync(previewId, folderId, request.Sample.ItemId, draft, ct);

            if (!result.HasPreview)
                return Results.Problem(result.Reason ?? "The preview could not be rendered.",
                    statusCode: StatusCodes.Status422UnprocessableEntity);

            var removedMs = result.Applied
                ? CanonicalWav.RemovedMs(result.OriginalBytes, result.OutputBytes)
                : (double?)null;

            return Results.Ok(new StepPreviewResponse(
                previewId,
                OriginalUrl: $"/preview-source/{Uri.EscapeDataString(folderId.Value)}/{request.Sample.ItemId:D}",
                ProcessedUrl: $"/audio-preview/{previewId}",
                removedMs,
                result.Reason,
                result.Applied));
        }

        private static async Task<IResult> GetRecentSamplesAsync(
            int? limit, IRecentAudioSampleFinder finder, CancellationToken ct)
        {
            var samples = await finder.FindAsync(Math.Clamp(limit ?? DefaultSampleLimit, 1, MaxSampleLimit), ct);
            return Results.Ok(samples.Select(s => new RecentAudioSampleDto(
                s.ParagraphItemId, s.Folder.Value, s.Text, s.CharacterName, s.VoiceName, s.ProjectTitle)).ToList());
        }

        /// <summary>Only the paragraph pipeline's steps live on this settings row.</summary>
        private static IResult? UnknownStep(string stepId)
        {
            var known = AudioPostProcessStepDefaults.For(StepScope.Paragraph).Select(s => s.StepId).ToList();
            return known.Contains(stepId, StringComparer.Ordinal)
                ? null
                : Results.Problem($"'{stepId}' is not a paragraph post-process step. Known steps: {string.Join(", ", known)}.",
                    statusCode: StatusCodes.Status400BadRequest);
        }
    }
}
