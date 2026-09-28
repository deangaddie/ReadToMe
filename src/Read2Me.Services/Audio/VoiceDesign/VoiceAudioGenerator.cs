using Read2Me.Core.Audio;
using Read2Me.Core.IO;
using Read2Me.Services.Audio.AudioCpp;
using Read2Me.Services.Llm;

namespace Read2Me.Services.Audio.VoiceDesign
{
    public sealed class VoiceAudioGenerator(
        VoiceDesignSettingsService settings,
        IVoiceDesignClientResolver clientResolver,
        IVoiceAudioWriter voiceAudio,
        IFileSystem fs) : IVoiceAudioGenerator
    {
        public async Task<VoiceGenerationResult> GenerateAsync(VoiceGenerationRequest request, CancellationToken ct)
        {
            var config = await settings.GetActiveConfigAsync();
            if (config == null)
            {
                return VoiceGenerationResult.Failure("No active voice design server configured.");
            }

            try
            {
                var storedSampleText = await settings.GetSampleTextAsync();
                var sampleText = string.IsNullOrWhiteSpace(storedSampleText)
                    ? PromptTemplates.VoiceDesignSampleSentence
                    : storedSampleText;

                var client = clientResolver.Resolve(config.Type);
                await using var audioStream = await client.DesignVoiceAsync(
                    config,
                    request.DesignPrompt,
                    sampleText,
                    request.SettingsOverrideJson,
                    ct);

                var storeReq = new AudioStoreRequest
                {
                    FolderId = request.FolderId,
                    CharacterId = request.CharacterId,
                    CharacterName = request.CharacterName,
                    CharacterAliases = request.CharacterAliases,
                    VoiceId = request.VoiceId,
                    VoiceName = request.VoiceName,
                    Source = audioStream,
                    Extension = ".wav",
                };

                // The take is stored and committed together: the writer owns that ordering, and
                // owns taking the file away again if the Book refuses to name it (ADR 0007). The
                // audio pipeline beneath it refuses a take over the hard Reference Limit.
                var fileName = await voiceAudio.RecordGeneratedAsync(
                    storeReq, sampleText, request.DesignPrompt, ct);

                // Measured as stored — after normalisation, the way the voice list reads it — so the
                // warning here and the badge there agree.
                return VoiceGenerationResult.Success(
                    fileName, sampleText, WavHeader.TryReadDurationMs(fs, request.FolderId, fileName));
            }
            catch (TtsBusyException)
            {
                return VoiceGenerationResult.Busy();
            }
            catch (ReferenceTooLongException ex)
            {
                // Nothing was stored. The upload's advice (trim it) does not fit a take whose length
                // the sample text decides.
                return VoiceGenerationResult.Failure(ReferenceLimit.HardLimitMessage(
                    ex.DurationMs, ex.ByteLength, "The generated voice",
                    "shorten the voice-design sample text and generate again."));
            }
            catch (Exception ex)
            {
                return VoiceGenerationResult.Failure(ex.Message);
            }
        }
    }
}
