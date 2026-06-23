namespace Read2Me.Services.Audio
{
    /// <summary>
    /// Probes an ffmpeg executable by running <c>ffmpeg -version</c> against a configured path.
    /// </summary>
    public interface IFfmpegProber
    {
        /// <summary>
        /// Runs <c>ffmpeg -version</c>. A null/blank path relies on PATH resolution.
        /// </summary>
        Task<FfmpegProbeResult> ProbeAsync(string? ffmpegPath, CancellationToken ct = default);
    }

    /// <summary>Outcome of an ffmpeg probe. <see cref="Message"/> carries the version banner or the error.</summary>
    public record FfmpegProbeResult(bool Success, string Message);
}
