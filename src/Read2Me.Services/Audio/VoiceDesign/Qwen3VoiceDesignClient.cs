using Read2Me.AppData.Entities;
using Read2Me.Services.Audio.AudioCpp;
using Read2Me.Services.Audio.VoiceDesign.Settings;

namespace Read2Me.Services.Audio.VoiceDesign
{
    /// <summary>
    /// Voice-design client for Qwen3-TTS VoiceDesign on audio.cpp (task <c>vdes</c>): the sample text
    /// is spoken, the design prompt is <c>options.instruction</c>, and there is no reference audio.
    /// Goes through the shared audio.cpp client, so it queues behind the audio queue at the TTS gate
    /// and throws <see cref="TtsBusyException"/> when another model holds the endpoint.
    /// </summary>
    public sealed class Qwen3VoiceDesignClient(IAudioCppClient audioCpp) : IVoiceDesignClient
    {
        public Task<Stream> DesignVoiceAsync(
            VoiceDesignServiceConfig config,
            string prompt,
            string sampleText,
            string? settingsOverrideJson,
            CancellationToken ct = default)
        {
            var cfg = VoiceDesignSettingsMerge.Merge<Qwen3VoiceDesignSettings>(
                config.SettingsJson, settingsOverrideJson);

            var options = Qwen3AudioCpp.Options(
                cfg.Temperature, cfg.TopP, cfg.TopK, cfg.RepetitionPenalty, cfg.MaxNewTokens, cfg.Seed);
            options["instruction"] = prompt;

            return audioCpp.SpeakAsync(cfg.BaseUrl,
                new AudioCppSpeechRequest(cfg.ModelId, sampleText, VoiceRef: null, ReferenceText: null, options,
                    Qwen3AudioCpp.Language(cfg.Language)), ct);
        }
    }
}
