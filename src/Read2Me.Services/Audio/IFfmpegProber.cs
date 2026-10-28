namespace Read2Me.Services.Audio
{
    /// <summary>
    /// Probes an ffmpeg executable, and the ffprobe beside it, by running <c>-version</c> against a configured path.
    /// </summary>
    public interface IFfmpegProber
    {
        /// <summary>
        /// Runs <c>ffmpeg -version</c>. A null/blank path relies on PATH resolution.
        /// </summary>
        Task<FfmpegProbeResult> ProbeAsync(string? ffmpegPath, CancellationToken ct = default);

        /// <summary>
        /// Runs <c>ffprobe -version</c> from beside the given ffmpeg (<see cref="FfmpegProber.ResolveFfprobe"/>),
        /// or from PATH when the path is null/blank.
        /// </summary>
        Task<FfmpegProbeResult> ProbeFfprobeAsync(string? ffmpegPath, CancellationToken ct = default);
    }

    /// <summary>Outcome of an ffmpeg probe. <see cref="Message"/> carries the version banner or the error.</summary>
    public record FfmpegProbeResult(bool Success, string Message);
}
