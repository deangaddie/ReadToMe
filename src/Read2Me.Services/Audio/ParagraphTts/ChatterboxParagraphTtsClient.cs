using System.Globalization;
using Read2Me.AppData.Entities;
using Read2Me.Services.Audio.AudioCpp;
using Read2Me.Services.Audio.ParagraphTts.Settings;
using Read2Me.Services.Audio.VoiceDesign.Settings;

namespace Read2Me.Services.Audio.ParagraphTts
{
    /// <summary>
    /// Paragraph TTS client for Chatterbox on audio.cpp (task <c>clon</c>): the voice's reference
    /// audio with no transcript. No free-text instruction channel — voiceInstructions and
    /// referenceTranscript are ignored; expression comes from the exaggeration/temperature knobs.
    /// audio.cpp, like upstream, conditions on only the first 6–10 s of the reference.
    /// </summary>
    public sealed class ChatterboxParagraphTtsClient(IAudioCppClient audioCpp) : IParagraphTtsClient
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
            var cfg = VoiceDesignSettingsMerge.Merge<ChatterboxParagraphTtsSettings>(
                settings.SettingsJson, settingsOverrideJson);

            // Every knob is sent, even at its default: audio.cpp's documented Chatterbox defaults
            // differ from what its code does (bt-07), so leaving one out is not "the same value".
            var options = new Dictionary<string, string>
            {
                ["exaggeration"] = Inv(cfg.Exaggeration),
                ["guidance_scale"] = Inv(cfg.CfgWeight),
                ["temperature"] = Inv(cfg.Temperature),
                ["min_p"] = Inv(cfg.MinP),
                ["top_p"] = Inv(cfg.TopP),
                ["repetition_penalty"] = Inv(cfg.RepetitionPenalty),
                ["seed"] = AudioCppSeed.For(cfg.Seed),
            };

            var voiceRef = new MemoryStream();
            await referenceAudioStream.CopyToAsync(voiceRef, ct);

            return await audioCpp.SpeakAsync(cfg.BaseUrl,
                new AudioCppSpeechRequest(cfg.ModelId, text, voiceRef.ToArray(), ReferenceText: null, options,
                    Language: "en"), ct);
        }

        private static string Inv(double value) => value.ToString(CultureInfo.InvariantCulture);
    }
}
