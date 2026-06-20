using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Read2Me.Core.Models;

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

        /// <summary>
        /// Stores generated paragraph audio. Returns relative path e.g. "audio/{paragraphItemId}.wav".
        /// </summary>
        Task<string> StoreParagraphAudioAsync(ProjectFolderId folderId, Guid paragraphItemId, Stream source, CancellationToken ct = default);
    }
}
