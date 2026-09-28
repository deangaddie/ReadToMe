using System.Globalization;

namespace Read2Me.Services.Audio
{
    /// <summary>Where a voice's reference audio sits against the <see cref="ReferenceLimit"/>.</summary>
    public enum ReferenceLimitVerdict
    {
        Within,

        /// <summary>Kept, with a warning.</summary>
        OverSoft,

        /// <summary>Refused: an upload is rejected, a generation fails.</summary>
        OverHard,
    }

    /// <summary>
    /// The <b>Reference Limit</b> (glossary <c>context/voice-rules.md</c>): the one app-wide bound on a
    /// Voice's reference audio, the tightest across every supported TTS provider. It is not per
    /// provider, because a Voice outlives the active provider. The hard limit pairs with the VoxCPM2
    /// session option <c>voxcpm2.audiovae_encoder_sample_capacity</c> (30 s) in audio.cpp's
    /// <c>server.json</c>; raise one and the other must follow.
    /// <para>
    /// Enforced where a reference is made — upload and voice design, both of which store through
    /// <see cref="FileAudioPipeline.StoreAsync"/> — and never by trimming, because trimming the audio
    /// would break its transcript.
    /// </para>
    /// </summary>
    public static class ReferenceLimit
    {
        public const double HardLimitMs = 30_000;
        public const long HardLimitBytes = 5L * 1024 * 1024;
        public const double SoftLimitMs = 15_000;

        public static ReferenceLimitVerdict Check(double durationMs, long byteLength) =>
            durationMs > HardLimitMs || byteLength > HardLimitBytes ? ReferenceLimitVerdict.OverHard
            : durationMs > SoftLimitMs ? ReferenceLimitVerdict.OverSoft
            : ReferenceLimitVerdict.Within;

        /// <summary>
        /// The warning shown wherever a voice's reference is displayed, or null within the soft limit
        /// (or when the duration is unknown). Being over it does not stop a Voice being ready.
        /// </summary>
        public static string? SoftWarning(double? durationMs) =>
            durationMs is { } ms && ms > SoftLimitMs
                ? $"The reference is {Seconds(ms)} s, over the {Seconds(SoftLimitMs)} s soft limit. A shorter clip clones more reliably."
                : null;

        /// <exception cref="ReferenceTooLongException">The reference is over the hard limit.</exception>
        public static void EnsureWithinHardLimit(double durationMs, long byteLength)
        {
            if (Check(durationMs, byteLength) == ReferenceLimitVerdict.OverHard)
                throw new ReferenceTooLongException(durationMs, byteLength);
        }

        /// <summary>A reference's length as the app reports it (tenths of a second), or null when unknown.</summary>
        public static double? ReportedSeconds(double? durationMs) =>
            durationMs is { } ms ? Math.Round(ms / 1000, 1) : null;

        /// <summary>
        /// Why a reference over the hard limit was refused: <paramref name="subject"/> names it ("The
        /// reference audio", "The generated voice") and <paramref name="advice"/> says what to do,
        /// which differs between an upload and a generation.
        /// </summary>
        public static string HardLimitMessage(double durationMs, long byteLength, string subject, string advice) =>
            byteLength > HardLimitBytes && durationMs <= HardLimitMs
                ? $"{subject} is {(byteLength / (1024.0 * 1024)).ToString("0.0", CultureInfo.InvariantCulture)} MiB; a voice's reference must be {HardLimitBytes / (1024 * 1024)} MiB or smaller — {advice}"
                : $"{subject} is {(durationMs / 1000).ToString("0.0", CultureInfo.InvariantCulture)} s; a voice's reference must be {Seconds(HardLimitMs)} s or shorter — {advice}";

        internal static string Seconds(double ms) =>
            (ms / 1000).ToString(ms % 1000 == 0 ? "0" : "0.0", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// A reference over the hard <see cref="ReferenceLimit"/>. Nothing was stored. The message is the
    /// upload's; a caller with other advice (voice design) words its own with
    /// <see cref="ReferenceLimit.HardLimitMessage"/>.
    /// </summary>
    public sealed class ReferenceTooLongException(double durationMs, long byteLength)
        : InvalidOperationException(ReferenceLimit.HardLimitMessage(
            durationMs, byteLength, "The reference audio", "trim it and upload again."))
    {
        public double DurationMs { get; } = durationMs;
        public long ByteLength { get; } = byteLength;
    }
}
