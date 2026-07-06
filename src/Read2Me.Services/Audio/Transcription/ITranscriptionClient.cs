using Read2Me.AppData.Entities;

namespace Read2Me.Services.Audio.Transcription
{
    /// <summary>
    /// Sends an audio file to a transcription backend and returns the transcript
    /// text. Each backend type (see <see cref="TranscriptionServiceType"/>) has
    /// its own implementation, selected at runtime by
    /// <see cref="ITranscriptionClientResolver"/>.
    /// </summary>
    public interface ITranscriptionClient
    {
        Task<string> TranscribeAsync(
            TranscriptionServiceConfig config,
            Stream audio,
            string fileName,
            CancellationToken ct = default);

        /// <summary>
        /// Transcribes with per-word timestamps, returning the words in spoken order.
        /// </summary>
        Task<IReadOnlyList<TranscribedWord>> TranscribeWithWordTimestampsAsync(
            TranscriptionServiceConfig config,
            Stream audio,
            string fileName,
            CancellationToken ct = default);
    }
}
