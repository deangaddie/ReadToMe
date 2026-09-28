using Read2Me.Core.Audio;
using Read2Me.Core.IO;
using Read2Me.Core.Models;

namespace Read2Me.Services.Audio
{
    public class FileAudioPipeline(
        IFileSystem fs,
        IAudioNormalizer normalizer,
        AudioProcessingSettingsService settingsService,
        IVoiceOriginalStore originals) : IAudioPipeline
    {
        /// <summary>
        /// The sole writer of fresh voice audio — upload, replace, regenerate and batch all funnel
        /// through here. That is why the stale-original delete lives at this chokepoint rather than at
        /// each call site: fresh audio makes a stored original stale, and a stale original is worse
        /// than none (it would leave the <c>Edited</c> chip lying and Restore pointing at audio the
        /// voice no longer has). A call site added later cannot forget.
        /// <para>
        /// The same chokepoint holds the hard <see cref="ReferenceLimit"/>: the normalised audio is
        /// measured before anything on disk changes, so a refused reference leaves the Voice exactly
        /// as it was — its audio and its stored original both untouched.
        /// </para>
        /// </summary>
        /// <exception cref="ReferenceTooLongException">The normalised audio is over the hard limit.</exception>
        public async Task<string> StoreAsync(AudioStoreRequest request, CancellationToken ct = default)
        {
            var settings = await settingsService.GetAsync();
            await using var normalizedAudio = await BufferAsync(
                await normalizer.NormalizeToWavAsync(request.Source, settings.FfmpegPath, ct), ct);

            // The header, as the voice list reads it, so the two never disagree at a limit; byte
            // arithmetic only when the header is unreadable.
            var durationMs = WavHeader.TryReadDurationMs(normalizedAudio)
                             ?? CanonicalWav.DurationMs((int)Math.Min(normalizedAudio.Length, int.MaxValue));
            normalizedAudio.Position = 0;
            ReferenceLimit.EnsureWithinHardLimit(durationMs, normalizedAudio.Length);

            originals.Delete(request.FolderId, request.CharacterId, request.VoiceId);

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

        /// <summary>The normaliser's stream, seekable so its length can be measured before it is written.</summary>
        private static async Task<Stream> BufferAsync(Stream normalized, CancellationToken ct)
        {
            if (normalized.CanSeek)
            {
                normalized.Position = 0;
                return normalized;
            }

            await using (normalized)
            {
                var buffer = new MemoryStream();
                await normalized.CopyToAsync(buffer, ct);
                buffer.Position = 0;
                return buffer;
            }
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
