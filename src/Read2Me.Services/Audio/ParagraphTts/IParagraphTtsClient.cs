using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Read2Me.AppData.Entities;

namespace Read2Me.Services.Audio.ParagraphTts
{
    /// <summary>
    /// Synthesises speech from text with voice cloning via a reference audio stream.
    /// Implementations are keyed by <see cref="ParagraphTtsServiceType"/> and selected
    /// at runtime via <see cref="IParagraphTtsClientResolver"/>.
    /// </summary>
    public interface IParagraphTtsClient
    {
        Task<Stream> GenerateAsync(
            string text,
            string? voiceInstructions,
            Stream referenceAudioStream,
            ParagraphTtsServiceConfig settings,
            string? settingsOverrideJson,
            CancellationToken ct = default);
    }
}
