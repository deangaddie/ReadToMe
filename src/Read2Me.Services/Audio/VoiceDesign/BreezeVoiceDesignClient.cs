using System.Globalization;
using Read2Me.AppData.Entities;
using Read2Me.Services.Audio.AudioCpp;
using Read2Me.Services.Audio.VoiceDesign.Settings;

namespace Read2Me.Services.Audio.VoiceDesign
{
    /// <summary>
    /// Voice-design client for Breeze TTS 2 on audio.cpp (task <c>tts</c>): the sample text is
    /// spoken, the DesignPrompt is <c>options.instruction</c>, and there is no reference audio.
    /// Goes through the shared audio.cpp client, so it queues behind the audio queue at the TTS
    /// gate and throws <see cref="TtsBusyException"/> when another model holds the endpoint.
    /// </summary>
    public sealed class BreezeVoiceDesignClient(IAudioCppClient audioCpp) : IVoiceDesignClient
    {
        public Task<Stream> DesignVoiceAsync(
            VoiceDesignServiceConfig config,
            string prompt,
            string sampleText,
            string? settingsOverrideJson,
            CancellationToken ct = default)
        {
            var cfg = BreezeVoiceDesignSettingsDiff.Apply(config.SettingsJson, settingsOverrideJson);

            var options = new Dictionary<string, string>
            {
                ["instruction"] = prompt,
                ["guidance_scale"] = cfg.GuidanceScale.ToString(CultureInfo.InvariantCulture),
                ["seed"] = AudioCppSeed.For(cfg.Seed),
            };

            return audioCpp.SpeakAsync(cfg.BaseUrl,
                new AudioCppSpeechRequest(cfg.ModelId, sampleText, VoiceRef: null, ReferenceText: null, options), ct);
        }
    }
}
