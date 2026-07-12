using Read2Me.Core.IO;
using Read2Me.Core.Models;

namespace Read2Me.Services.Audio
{
    /// <summary>One cached Preview Source: which item it came from, and when it was generated.</summary>
    public sealed record PreviewSourceEntry(
        ProjectFolderId Folder,
        Guid ParagraphItemId,
        string Path,
        DateTime LastWriteTimeUtc);

    /// <summary>
    /// The capped rolling store of <b>Preview Sources</b> — paragraph audio as it is after loudness
    /// normalise but <i>before</i> any Audio Post-Process Step. The stored <c>{id}.wav</c> cannot serve
    /// this role: it is already post-processed, so previewing a step against it stacks the step on
    /// itself. Written by <see cref="AudioItemPipeline"/>, read by the A/B preview.
    /// </summary>
    public interface IPreviewSourceCache
    {
        /// <summary>Stores the item's Preview Source, then evicts all but the newest <see cref="PreviewSourceCache.Capacity"/> entries.</summary>
        Task SaveAsync(ProjectFolderId folder, Guid itemId, byte[] wav, CancellationToken ct = default);

        /// <summary>Every cached Preview Source, newest first.</summary>
        IReadOnlyList<PreviewSourceEntry> List();

        /// <summary>The item's Preview Source bytes, or null once it has been evicted.</summary>
        Task<byte[]?> TryReadAsync(ProjectFolderId folder, Guid itemId, CancellationToken ct = default);

        /// <summary>The file backing the item's Preview Source, for callers that must serve it as a file.</summary>
        bool TryGetPath(ProjectFolderId folder, Guid itemId, out string? path);
    }

    public sealed class PreviewSourceCache(IFileSystem fs) : IPreviewSourceCache
    {
        /// Dot-prefixed so ListProjectFolders skips it and the static-file provider will not serve it.
        internal const string DirectoryName = ".preview-src";

        /// ~25 MB at 10 s per item. The picker offers far fewer than this, so a working session
        /// never evicts a sample the user was about to audition.
        internal const int Capacity = 50;

        private const string Separator = "__";

        private string CacheDirectory => Path.Combine(fs.WorkspacePath, DirectoryName);

        public async Task SaveAsync(ProjectFolderId folder, Guid itemId, byte[] wav, CancellationToken ct = default)
        {
            if (!TryResolve(folder, itemId, out var path))
                return;

            fs.EnsureDirectory(CacheDirectory);

            using var source = new MemoryStream(wav);
            await fs.WriteFileAsync(path!, source);

            Evict();
        }

        public IReadOnlyList<PreviewSourceEntry> List() =>
            fs.ListFiles(CacheDirectory, "*.wav")
                .Select(Parse)
                .OfType<PreviewSourceEntry>()
                .OrderByDescending(e => e.LastWriteTimeUtc)
                .ToList();

        public async Task<byte[]?> TryReadAsync(ProjectFolderId folder, Guid itemId, CancellationToken ct = default)
        {
            if (!TryGetPath(folder, itemId, out var path))
                return null;

            using var source = fs.OpenRead(path!);
            using var buffer = new MemoryStream();
            await source.CopyToAsync(buffer, ct);
            return buffer.ToArray();
        }

        public bool TryGetPath(ProjectFolderId folder, Guid itemId, out string? path)
        {
            if (TryResolve(folder, itemId, out path) && fs.FileExists(path!))
                return true;

            // TryResolve names a file whether or not it exists; a caller that gets false must not be
            // handed a path anyway.
            path = null;
            return false;
        }

        /// The folder name can reach us from a URL through <see cref="ProjectFolderId"/>'s implicit
        /// string conversion, so it is checked again here — the last point before it becomes a path.
        private bool TryResolve(ProjectFolderId folder, Guid itemId, out string? path)
        {
            path = null;

            if (!ProjectFolderId.TryParse(folder.Value, out _))
                return false;

            path = Path.Combine(CacheDirectory, FileName(folder, itemId));
            return true;
        }

        private void Evict()
        {
            foreach (var stale in List().Skip(Capacity))
                fs.DeleteFile(stale.Path);
        }

        private static string FileName(ProjectFolderId folder, Guid itemId) =>
            $"{folder.Value}{Separator}{itemId:D}.wav";

        /// The id is a fixed-length GUID at the end, so it is parsed from the right — a folder name
        /// that itself contains the separator cannot shift the split.
        private static PreviewSourceEntry? Parse(FileEntry file)
        {
            var name = Path.GetFileNameWithoutExtension(file.Path);
            const int guidLength = 36;

            if (name.Length < guidLength + Separator.Length + 1)
                return null;

            var folderName = name[..^(guidLength + Separator.Length)];
            if (!name.AsSpan(folderName.Length, Separator.Length).SequenceEqual(Separator))
                return null;

            return Guid.TryParseExact(name[^guidLength..], "D", out var itemId)
                ? new PreviewSourceEntry(new ProjectFolderId(folderName), itemId, file.Path, file.LastWriteTimeUtc)
                : null;
        }
    }
}
