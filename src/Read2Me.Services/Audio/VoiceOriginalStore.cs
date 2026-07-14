using Read2Me.Core.IO;
using Read2Me.Core.Models;

namespace Read2Me.Services.Audio
{
    /// <summary>
    /// The voice's <b>unprocessed original</b> — the audio as it was before the voice audio editor
    /// first wrote over it. It holds one invariant, and everything else in the feature reads off it:
    /// <para>
    /// <b><c>{voiceId}.orig.wav</c> exists ⟺ the voice's audio has been edited.</b>
    /// </para>
    /// No DB column and no flag: the path is a pure function of the row, so the <c>Edited</c> chip,
    /// <c>Restore original</c>, and the regenerate confirm are each one <see cref="Exists"/>.
    /// <para>
    /// Keyed on the <b>voice id alone</b>, unlike the live WAV, whose name
    /// <see cref="FileAudioPipeline.StoreAsync"/> derives from the voice's <i>name</i> — so renaming a
    /// voice and re-uploading moves the live file while the original stays put. It sits next to the
    /// live WAV inside the project folder, which means the existing static <c>/workspace</c> mount
    /// serves it for free: the editor's "before" player is a plain <c>&lt;audio src&gt;</c>.
    /// </para>
    /// </summary>
    public interface IVoiceOriginalStore
    {
        /// <summary>The invariant. True iff this voice's audio has been edited.</summary>
        bool Exists(ProjectFolderId folder, Guid characterId, Guid voiceId);

        /// <summary>Project-relative, forward-slashed — as <c>Voice.AudioFileName</c> is.</summary>
        string RelativePath(Guid characterId, Guid voiceId);

        Task<byte[]?> TryReadAsync(ProjectFolderId folder, Guid characterId, Guid voiceId, CancellationToken ct = default);

        /// <summary>
        /// Copies the live WAV aside on the <b>first</b> Apply and never again — a second Apply must not
        /// overwrite the original with already-edited audio, or a re-edit would stack filters on filters
        /// and Restore would restore the wrong bytes. A byte copy, never a re-conform: the live WAV was
        /// already loudnorm'd to Canonical WAV when it was stored.
        /// </summary>
        Task CaptureIfAbsentAsync(ProjectFolderId folder, Guid characterId, Guid voiceId,
                                  string liveRelativePath, CancellationToken ct = default);

        /// <summary>Drops the original. A no-op when there is none.</summary>
        void Delete(ProjectFolderId folder, Guid characterId, Guid voiceId);
    }

    public sealed class VoiceOriginalStore(IFileSystem fs) : IVoiceOriginalStore
    {
        public string RelativePath(Guid characterId, Guid voiceId) =>
            $"voices/{characterId}/{voiceId}.orig.wav";

        public bool Exists(ProjectFolderId folder, Guid characterId, Guid voiceId) =>
            fs.FileExists(FullPath(folder, RelativePath(characterId, voiceId)));

        public async Task<byte[]?> TryReadAsync(
            ProjectFolderId folder, Guid characterId, Guid voiceId, CancellationToken ct = default)
        {
            var path = FullPath(folder, RelativePath(characterId, voiceId));
            if (!fs.FileExists(path)) return null;

            using var source = fs.OpenRead(path);
            using var buffer = new MemoryStream();
            await source.CopyToAsync(buffer, ct);
            return buffer.ToArray();
        }

        public async Task CaptureIfAbsentAsync(
            ProjectFolderId folder, Guid characterId, Guid voiceId, string liveRelativePath,
            CancellationToken ct = default)
        {
            var originalPath = FullPath(folder, RelativePath(characterId, voiceId));
            if (fs.FileExists(originalPath)) return;

            var livePath = FullPath(folder, liveRelativePath);
            if (!fs.FileExists(livePath)) return;

            fs.EnsureDirectory(Path.GetDirectoryName(originalPath)!);

            using var live = fs.OpenRead(livePath);
            await fs.WriteFileAsync(originalPath, live);
        }

        public void Delete(ProjectFolderId folder, Guid characterId, Guid voiceId)
        {
            var path = FullPath(folder, RelativePath(characterId, voiceId));
            if (fs.FileExists(path))
                fs.DeleteFile(path);
        }

        private string FullPath(ProjectFolderId folder, string relativePath) =>
            Path.Combine(
                fs.GetProjectFolderPath(folder.Value),
                relativePath.Replace('/', Path.DirectorySeparatorChar));
    }
}
