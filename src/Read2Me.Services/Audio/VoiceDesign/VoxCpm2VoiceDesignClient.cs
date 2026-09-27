using Read2Me.AppData.Entities;
using Read2Me.Services.Audio.AudioCpp;
using Read2Me.Services.Audio.VoiceDesign.Settings;

namespace Read2Me.Services.Audio.VoiceDesign
{
    /// <summary>
    /// Voice-design client for VoxCPM2 on audio.cpp: no reference audio, and the design prompt is
    /// the control, spoken as <c>(prompt)sampleText</c>. Goes through the shared audio.cpp client,
    /// so it queues behind the audio queue at the TTS gate and throws <see cref="TtsBusyException"/>
    /// when another model holds the endpoint.
    /// </summary>
    public sealed class VoxCpm2VoiceDesignClient(IAudioCppClient audioCpp) : IVoiceDesignClient
    {
        public Task<Stream> DesignVoiceAsync(
            VoiceDesignServiceConfig config,
            string prompt,
            string sampleText,
            string? settingsOverrideJson,
            CancellationToken ct = default)
        {
            var cfg = VoiceDesignSettingsMerge.Merge<VoxCpm2VoiceDesignSettings>(
                config.SettingsJson, settingsOverrideJson);

            var options = VoxCpm2AudioCpp.Options(
                cfg.CfgValue, cfg.InferenceTimesteps, cfg.MinLen, cfg.MaxLen,
                cfg.RetryBadcase, cfg.RetryBadcaseMaxTimes, cfg.RetryBadcaseRatioThreshold, cfg.Seed);

            return audioCpp.SpeakAsync(cfg.BaseUrl,
                new AudioCppSpeechRequest(cfg.ModelId, VoxCpm2AudioCpp.Input(sampleText, prompt),
                    VoiceRef: null, ReferenceText: null, options), ct);
        }
    }
}
