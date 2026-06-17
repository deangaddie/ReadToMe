using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Read2Me.AppData.Entities;

namespace Read2Me.Services.Audio
{
    /// <summary>
    /// Sends an audio file to a transcription service and returns the transcript text.
    /// </summary>
    public interface ITranscriptionClient
    {
        Task<string> TranscribeAsync(
            AudioServerConfig config,
            Stream audio,
            string fileName,
            CancellationToken ct = default);
    }
}
