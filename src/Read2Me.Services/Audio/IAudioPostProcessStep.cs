namespace Read2Me.Services.Audio
{
    /// <summary>
    /// Result of a post-process step run. <see cref="Audio"/> is the step output when
    /// <see cref="Applied"/> is true, otherwise the input audio unchanged.
    /// <see cref="Reason"/> is set only when the step was skipped.
    /// </summary>
    public sealed record PostProcessResult(byte[] Audio, bool Applied, string? Reason);

    /// <summary>
    /// One optional, cosmetic audio post-process step applied to paragraph-item audio
    /// between loudness normalize and verify. Implementations must never throw and never
    /// lose audio: on any failure they return the input unchanged with
    /// <see cref="PostProcessResult.Applied"/> false and a reason (the same never-throw
    /// failure semantics as <see cref="IAudioNormalizer"/>).
    /// </summary>
    public interface IAudioPostProcessStep
    {
        string StepId { get; }

        /// <summary>
        /// Processes <paramref name="wav"/>. <paramref name="ffmpegPath"/> is the configured
        /// executable path, or null/blank to rely on PATH. <paramref name="settingsJson"/> is
        /// this step's settings payload from the stored step config, or null for defaults.
        /// </summary>
        Task<PostProcessResult> ProcessAsync(byte[] wav, string? ffmpegPath, string? settingsJson, CancellationToken ct);
    }
}
