using System.Threading;
using System.Threading.Tasks;

namespace Read2Me.Core.Audio
{
    /// <summary>
    /// Stores a voice audio file in the project folder.
    /// Returns the relative filename (e.g. "voices/{charId}/{voiceId}-name.wav").
    /// Future stages (format normalisation, bitrate) will be added here.
    /// </summary>
    public interface IAudioPipeline
    {
        Task<string> StoreAsync(AudioStoreRequest request, CancellationToken ct = default);
    }
}
