using Read2Me.AppData.Entities;

namespace Read2Me.Services.Audio.VoiceDesign
{
    /// <summary>
    /// Synthesises a new voice from a text prompt and returns a WAV audio stream.
    /// Implementations are keyed by <see cref="VoiceDesignServiceType"/> and selected
    /// at runtime via <see cref="IVoiceDesignClientResolver"/>.
    /// </summary>
    public interface IVoiceDesignClient
    {
        Task<Stream> DesignVoiceAsync(
            VoiceDesignServiceConfig config,
            string prompt,
            string sampleText,
            string? settingsOverrideJson,
            CancellationToken ct = default);
    }
}
