using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Read2Me.Core.Audio;
using Read2Me.Core.IO;
using Read2Me.Core.Models;

namespace Read2Me.Services.Audio
{
    public class FileAudioPipeline(IFileSystem fs, IAudioNormalizer normalizer, AudioProcessingSettingsService settingsService) : IAudioPipeline
    {
        public async Task<string> StoreAsync(AudioStoreRequest request, CancellationToken ct = default)
        {
            var settings = await settingsService.GetAsync();
            var normalizedAudio = await normalizer.NormalizeToWavAsync(request.Source, settings.FfmpegPath, ct);

            var projectFolder = fs.GetProjectFolderPath(request.FolderId.Value);
            var charFolder = Path.Combine(projectFolder, "voices", request.CharacterId.ToString());
            fs.EnsureDirectory(charFolder);

            await WriteHelperTextFileIfAbsentAsync(charFolder, request);

            var sanitizedVoiceName = NameSanitizer.Sanitize(request.VoiceName);
            if (string.IsNullOrEmpty(sanitizedVoiceName))
                sanitizedVoiceName = request.VoiceId.ToString("N")[..8];

            var fileName = $"{request.VoiceId}-{sanitizedVoiceName}.wav";
            var fullPath = Path.Combine(charFolder, fileName);

            await fs.WriteFileAsync(fullPath, normalizedAudio);

            return Path.Combine("voices", request.CharacterId.ToString(), fileName)
                       .Replace('\\', '/');
        }

        public async Task<string> StoreParagraphAudioAsync(ProjectFolderId folderId, Guid paragraphItemId, Stream source, CancellationToken ct = default)
        {
            var projectFolder = fs.GetProjectFolderPath(folderId.Value);
            var audioFolder = Path.Combine(projectFolder, "audio");
            fs.EnsureDirectory(audioFolder);

            var fileName = $"{paragraphItemId}.wav";
            await fs.WriteFileAsync(Path.Combine(audioFolder, fileName), source);

            return $"audio/{fileName}";
        }

        private async Task WriteHelperTextFileIfAbsentAsync(string charFolder, AudioStoreRequest request)
        {
            var sanitizedCharName = NameSanitizer.Sanitize(request.CharacterName);
            if (string.IsNullOrEmpty(sanitizedCharName))
                sanitizedCharName = request.CharacterId.ToString("N")[..8];

            var txtPath = Path.Combine(charFolder, sanitizedCharName + ".txt");
            if (fs.FileExists(txtPath)) return;

            var lines = new System.Collections.Generic.List<string> { request.CharacterName };
            foreach (var alias in request.CharacterAliases)
                lines.Add(alias);

            await fs.WriteAllLinesAsync(txtPath, lines);
        }
    }
}
