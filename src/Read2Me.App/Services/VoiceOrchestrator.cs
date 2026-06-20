using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Read2Me.Core.Audio;
using Read2Me.Core.IO;
using Read2Me.Core.Models;
using Read2Me.Services;
using Read2Me.Services.Audio.Transcription;
using Read2Me.Services.Audio.VoiceDesign;
using Read2Me.Services.Voice;

namespace Read2Me.App.Services
{
    public class VoiceOrchestrator(
        IAudioPipeline audioPipeline,
        ITranscriptionClientResolver transcriptionResolver,
        IVoiceAudioGenerator voiceAudioGenerator,
        TranscriptionSettingsService transcriptionSettings,
        VoiceDesignPromptService voiceDesignPromptService,
        IFileSystem fileSystem)
    {
        public async Task<string> StoreAudioAsync(
            AudioStoreRequest request,
            CancellationToken ct = default) =>
            await audioPipeline.StoreAsync(request, ct);

        public async Task<string> TranscribeAsync(
            ProjectFolderId folder,
            Guid voiceId,
            Stream audioStream,
            string fileName,
            CancellationToken ct = default)
        {
            var config = await transcriptionSettings.GetActiveConfigAsync();
            if (config == null)
                throw new InvalidOperationException("No active transcription server configured.");

            var client = transcriptionResolver.Resolve(config.Type);
            return await client.TranscribeAsync(config, audioStream, fileName, ct);
        }

        public async Task<string> GenerateWithPromptAsync(
            string renderedPrompt,
            CancellationToken ct = default)
        {
            var result = await voiceDesignPromptService.GenerateWithPromptAsync(renderedPrompt, ct);
            if (result.Status == VoiceDesignPromptService.GenerateStatus.Success && result.Prompt is not null)
                return result.Prompt;
            throw new InvalidOperationException(result.FailureReason ?? "Failed to generate voice prompt.");
        }

        public async Task<string> BuildRenderedPromptAsync(
            string bookTitle,
            string author,
            string characterName) =>
            await voiceDesignPromptService.BuildRenderedPromptAsync(bookTitle, author, characterName);

        public async Task<VoiceGenerationResult> GenerateVoiceAudioAsync(
            VoiceGenerationRequest request,
            CancellationToken ct = default) =>
            await voiceAudioGenerator.GenerateAsync(request, ct);

        public Stream? OpenAudioStream(ProjectFolderId folder, string? audioFileName)
        {
            if (audioFileName == null) return null;
            var projectFolder = fileSystem.GetProjectFolderPath(folder.Value);
            var path = System.IO.Path.Combine(projectFolder, audioFileName.Replace('/', System.IO.Path.DirectorySeparatorChar));
            if (!fileSystem.FileExists(path)) return null;
            return File.OpenRead(path);
        }
    }
}
