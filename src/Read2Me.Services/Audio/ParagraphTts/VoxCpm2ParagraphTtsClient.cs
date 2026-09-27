using Read2Me.AppData.Entities;
using Read2Me.Services.Audio.AudioCpp;
using Read2Me.Services.Audio.ParagraphTts.Settings;
using Read2Me.Services.Audio.VoiceDesign.Settings;

namespace Read2Me.Services.Audio.ParagraphTts
{
    /// <summary>
    /// Paragraph TTS client for VoxCPM2 on audio.cpp: controllable cloning — the voice's reference
    /// audio with <b>no</b> transcript (a transcript would switch VoxCPM2 to "ultimate" cloning,
    /// which ignores the control), and the item's VoiceInstructions as the <c>(control)text</c> prefix.
    /// </summary>
    public sealed class VoxCpm2ParagraphTtsClient(IAudioCppClient audioCpp) : IParagraphTtsClient
    {
        public async Task<Stream> GenerateAsync(
            string text,
            string? voiceInstructions,
            Stream referenceAudioStream,
            ParagraphTtsServiceConfig settings,
            string? settingsOverrideJson,
            string? referenceTranscript = null,
            CancellationToken ct = default)
        {
            var cfg = VoiceDesignSettingsMerge.Merge<VoxCpm2ParagraphTtsSettings>(
                settings.SettingsJson, settingsOverrideJson);

            var options = VoxCpm2AudioCpp.Options(
                cfg.CfgValue, cfg.InferenceTimesteps, cfg.MinLen, cfg.MaxLen,
                cfg.RetryBadcase, cfg.RetryBadcaseMaxTimes, cfg.RetryBadcaseRatioThreshold, cfg.Seed);

            var voiceRef = new MemoryStream();
            await referenceAudioStream.CopyToAsync(voiceRef, ct);

            return await audioCpp.SpeakAsync(cfg.BaseUrl,
                new AudioCppSpeechRequest(cfg.ModelId, VoxCpm2AudioCpp.Input(text, voiceInstructions),
                    voiceRef.ToArray(), ReferenceText: null, options), ct);
        }
    }
}
