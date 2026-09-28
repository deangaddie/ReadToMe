using Read2Me.AppData.Entities;
using Read2Me.Services.Audio.AudioCpp;
using Read2Me.Services.Audio.ParagraphTts.Settings;
using Read2Me.Services.Audio.VoiceDesign.Settings;

namespace Read2Me.Services.Audio.ParagraphTts
{
    /// <summary>
    /// Paragraph TTS client for Qwen3-Base voice cloning on audio.cpp (task <c>tts</c>): the voice's
    /// reference audio plus its transcript as <c>reference_text</c>, which puts Qwen3 in ICL mode.
    /// No free-text instruction channel — voiceInstructions are ignored.
    /// </summary>
    public sealed class Qwen3ParagraphTtsClient(IAudioCppClient audioCpp) : IParagraphTtsClient
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
            // ICL cloning is conditioned on the reference transcript; fail here with a clear message
            // rather than send a clone audio.cpp would condition on an empty one.
            if (string.IsNullOrWhiteSpace(referenceTranscript))
                throw new InvalidOperationException(
                    "Qwen3-Base voice cloning requires a reference-audio transcript. " +
                    "Set the voice's transcript (or the voice-design sample text).");

            var cfg = VoiceDesignSettingsMerge.Merge<Qwen3ParagraphTtsSettings>(
                settings.SettingsJson, settingsOverrideJson);

            var options = Qwen3AudioCpp.Options(
                cfg.Temperature, cfg.TopP, cfg.TopK, cfg.RepetitionPenalty, cfg.MaxNewTokens, cfg.Seed);

            var voiceRef = new MemoryStream();
            await referenceAudioStream.CopyToAsync(voiceRef, ct);

            return await audioCpp.SpeakAsync(cfg.BaseUrl,
                new AudioCppSpeechRequest(cfg.ModelId, text, voiceRef.ToArray(), referenceTranscript, options,
                    Qwen3AudioCpp.Language(cfg.Language)), ct);
        }
    }
}
