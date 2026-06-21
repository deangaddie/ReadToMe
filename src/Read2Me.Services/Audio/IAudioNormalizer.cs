using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Read2Me.Services.Audio
{
    /// <summary>Outcome status of a normalization attempt.</summary>
    public enum NormalizeStatus
    {
        Normalized,
        Skipped,
    }

    /// <summary>
    /// Result of a normalization attempt. <see cref="Audio"/> is always a seekable, rewound
    /// stream — the normalized bytes when <see cref="Status"/> is <see cref="NormalizeStatus.Normalized"/>,
    /// otherwise the original audio. <see cref="Reason"/> is set only on <see cref="NormalizeStatus.Skipped"/>.
    /// </summary>
    public record NormalizeResult(NormalizeStatus Status, Stream Audio, string? Reason);

    /// <summary>
    /// Normalizes audio loudness to a broadcast standard. Implementations must never throw and
    /// never lose audio: on any failure they return <see cref="NormalizeStatus.Skipped"/> with the
    /// original audio intact.
    /// </summary>
    public interface IAudioNormalizer
    {
        /// <summary>
        /// Normalizes <paramref name="wav"/> loudness. <paramref name="ffmpegPath"/> is the configured
        /// executable path, or null/blank to rely on PATH.
        /// </summary>
        Task<NormalizeResult> NormalizeAsync(Stream wav, string? ffmpegPath, CancellationToken ct = default);
    }
}
