namespace Read2Me.Services.Audio.Assembly
{
    public interface IAudiobookEncoder
    {
        /// <summary>
        /// Returns the duration of a WAV file via ffprobe.
        /// Throws <see cref="InvalidOperationException"/> when ffprobe is absent or fails.
        /// </summary>
        Task<TimeSpan> GetDurationAsync(string wavPath, string? ffmpegPath, CancellationToken ct = default);

        /// <summary>
        /// Returns the path to a canonical-format (24 kHz / mono / 16-bit PCM) silence WAV of
        /// exactly <paramref name="ms"/> milliseconds. Results are cached per distinct ms for the
        /// lifetime of this instance. Caller owns temp-file cleanup after the assembly run.
        /// </summary>
        Task<string> GetSilenceAsync(int ms, string? ffmpegPath, CancellationToken ct = default);

        /// <summary>
        /// Encodes a finished concat-list + ffmetadata file to an m4b, reporting encode progress
        /// as a 0..1 fraction via <paramref name="progress"/>. The <paramref name="outputPath"/>
        /// should be the final destination (the caller wraps it in .tmp + rename if desired).
        /// </summary>
        Task EncodeAsync(
            string concatListPath,
            string ffmetadataPath,
            string? coverImagePath,
            string outputPath,
            TimeSpan totalDuration,
            IProgress<double>? progress,
            string? ffmpegPath,
            CancellationToken ct = default);
    }
}
