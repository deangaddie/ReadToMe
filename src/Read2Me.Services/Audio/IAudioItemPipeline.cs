using Read2Me.AppData.Entities;
using Read2Me.Core.Models;

namespace Read2Me.Services.Audio
{
    public record NormalizeOutcome(bool Ok, string? Reason);

    public record VerifyOutcome(bool Ok, double? Wer, string? Reason, string? Transcript, bool Rescued);

    public record PipelineResult(
        byte[] AudioBytes,
        NormalizeOutcome Normalize,
        VerifyOutcome Verify);

    public record PipelineRequest(
        ProjectFolderId Folder,
        Guid ParagraphItemId,
        string SourceText,
        string? VoiceInstructions,
        string RefAudioPath,
        ParagraphTtsServiceConfig TtsConfig,
        string? TtsSettingsOverrideJson,
        int MaxAttempts,
        double WerThreshold,
        string? FfmpegPath,
        string? Speaker,
        string? ReferenceTranscript = null);

    public interface IAudioItemPipeline
    {
        Task<PipelineResult> RunAsync(PipelineRequest request, CancellationToken ct);
    }
}
