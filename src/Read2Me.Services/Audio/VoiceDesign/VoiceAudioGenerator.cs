using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Read2Me.Core.Audio;
using Read2Me.Core.Models;
using Read2Me.Services.Llm;

namespace Read2Me.Services.Audio.VoiceDesign
{
    public sealed class VoiceAudioGenerator(
        VoiceDesignSettingsService settings,
        IVoiceDesignClientResolver clientResolver,
        IAudioPipeline audioPipeline,
        IBookCommandHandler commandHandler)
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
                var fileName = await audioPipeline.StoreAsync(storeReq, ct);

                await commandHandler.ExecuteAsync(new SetVoiceGeneratedCommand(
                    request.FolderId,
                    request.VoiceId,
                    fileName,
                    sampleText,
                    request.DesignPrompt), ct);

                return VoiceGenerationResult.Success(fileName, sampleText);
            }
            catch (Exception ex)
            {
                return VoiceGenerationResult.Failure(ex.Message);
            }
        }
    }
}
