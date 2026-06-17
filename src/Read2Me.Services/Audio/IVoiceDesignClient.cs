using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Read2Me.AppData.Entities;

namespace Read2Me.Services.Audio
{
    /// <summary>
    /// Sends a text prompt to a voice-design service and returns the generated audio stream.
    /// </summary>
    public interface IVoiceDesignClient
    {
        Task<Stream> DesignVoiceAsync(
            AudioServerConfig config,
            string prompt,
            string sampleText,
            CancellationToken ct = default);
    }
}
