using Read2Me.AppData.Entities;
using Read2Me.Core.Models;
using Read2Me.Services.Queueing;

namespace Read2Me.Services.Audio
{
    public record NormalizeOutcome(bool Ok, string? Reason);

    public record VerifyOutcome(bool Ok, double? Wer, string? Reason, string? Transcript, bool Rescued);

    /// <param name="Outcome">
    /// How the run ended as far as a retry decision is concerned — <b>provider behaviour only</b>.
    /// A run that produced audio the verifier rejected is still <see cref="WorkOutcome.Ok"/>: the
    /// quality verdict lives in <paramref name="Verify"/>, and whether the item completes is decided
    /// after apply. On anything but <see cref="WorkOutcome.Ok"/> the other three fields are the
    /// empty/failed placeholders — there is no audio to record.
    /// </param>
    public record PipelineResult(
        byte[] AudioBytes,
        NormalizeOutcome Normalize,
        VerifyOutcome Verify,
        WorkOutcome Outcome);

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
        /// <summary>
        /// Runs one item end to end. <b>Total</b>: every non-cancellation failure comes back as
        /// <see cref="PipelineResult.Outcome"/>, never as an exception — the same contract
        /// <c>ILlmCompletionRunner</c> offers on the LLM side. Only genuine cancellation of
        /// <paramref name="ct"/> still throws <see cref="OperationCanceledException"/> through.
        /// </summary>
        Task<PipelineResult> RunAsync(PipelineRequest request, CancellationToken ct);
    }
}
