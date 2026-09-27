using System.Globalization;
using Read2Me.AppData.Entities;
using Read2Me.Services.Audio.AudioCpp;
using Read2Me.Services.Audio.ParagraphTts.Settings;
using Read2Me.Services.Audio.VoiceDesign.Settings;

namespace Read2Me.Services.Audio.ParagraphTts
{
    /// <summary>
    /// Paragraph TTS client for Breeze TTS 2 voice cloning on audio.cpp (task <c>clon</c>). The
    /// item's VoiceInstructions pass through verbatim as <c>options.instruction</c>, with the
    /// instructed guidance scale; an item without them omits the instruction — audio.cpp then uses
    /// its neutral default — and takes the cheaper plain scale.
    /// </summary>
    public sealed class BreezeParagraphTtsClient(IAudioCppClient audioCpp) : IParagraphTtsClient
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
            var cfg = VoiceDesignSettingsMerge.Merge<BreezeParagraphTtsSettings>(
                settings.SettingsJson, settingsOverrideJson);

            var instructed = !string.IsNullOrWhiteSpace(voiceInstructions);
            var options = new Dictionary<string, string>
            {
                ["guidance_scale"] = Inv(instructed ? cfg.InstructedGuidanceScale : cfg.PlainGuidanceScale),
                ["temperature"] = Inv(cfg.Temperature),
                ["top_k"] = cfg.TopK.ToString(CultureInfo.InvariantCulture),
                ["top_p"] = Inv(cfg.TopP),
                ["seed"] = AudioCppSeed.For(cfg.Seed),
            };
            if (instructed)
                options["instruction"] = voiceInstructions!;

            var voiceRef = new MemoryStream();
            await referenceAudioStream.CopyToAsync(voiceRef, ct);

            return await audioCpp.SpeakAsync(cfg.BaseUrl,
                new AudioCppSpeechRequest(cfg.ModelId, text, voiceRef.ToArray(), referenceTranscript, options), ct);
        }

        private static string Inv(double value) => value.ToString(CultureInfo.InvariantCulture);
    }
}
