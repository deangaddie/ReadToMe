namespace Read2Me.Services.Audio
{
    /// <summary>
    /// The WAV format every ffmpeg write in the app emits: 24 kHz, mono, 16-bit PCM. Because
    /// the format is fixed, a clip's duration is byte arithmetic — no prober, no second pass.
    /// </summary>
    public static class CanonicalWav
    {
        public const int SampleRateHz = 24000;
        public const int BytesPerSample = 2;
        public const int BytesPerSecond = SampleRateHz * BytesPerSample;

        /// <summary>Bytes of RIFF/fmt/data headers preceding the PCM payload.</summary>
        public const int HeaderBytes = 44;

        /// <summary>
        /// The ffmpeg args every write must carry. Not optional: <c>loudnorm</c> resamples
        /// internally to 192 kHz, and an unqualified WAV write inherits that rate.
        /// </summary>
        public static readonly string[] FormatArgs = ["-ar", "24000", "-ac", "1", "-c:a", "pcm_s16le"];

        /// <summary>Duration of a canonical-WAV byte payload. Sub-header lengths read as zero.</summary>
        public static double DurationMs(int byteLength) =>
            Math.Max(0, byteLength - HeaderBytes) * 1000.0 / BytesPerSecond;

        /// <summary>Audio removed between two canonical WAVs, never negative.</summary>
        public static double RemovedMs(int originalBytes, int outputBytes) =>
            Math.Max(0, DurationMs(originalBytes) - DurationMs(outputBytes));
    }
}
