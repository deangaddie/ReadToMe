namespace Read2Me.Core.IO
{
    /// <summary>A file on disk with the timestamp callers order by ("most recent first").</summary>
    public readonly record struct FileEntry(string Path, DateTime LastWriteTimeUtc);

    public interface IFileSystem
    {
        /// <summary>The workspace root. Scratch stores that are not projects live in dot-prefixed dirs under it.</summary>
        string WorkspacePath { get; }

        /// <summary>
        /// Project folders under the workspace. Dot-prefixed directories are excluded — they are
        /// scratch stores (or tooling like <c>.git</c>), never projects.
        /// </summary>
        IReadOnlyList<string> ListProjectFolders();

        /// <summary>
        /// Files matching <paramref name="searchPattern"/> directly under <paramref name="directoryPath"/>,
        /// in no particular order. Returns empty when the directory does not exist.
        /// </summary>
        IReadOnlyList<FileEntry> ListFiles(string directoryPath, string searchPattern);
        bool ProjectFolderExists(string name);
        string GetProjectFolderPath(string name);
        void CreateProjectFolder(string name);
        void DeleteProjectFolder(string name);

        bool FileExists(string path);
        Stream OpenRead(string path);
        void EnsureDirectory(string path);
        void DeleteFile(string path);

        /// <summary>
        /// Moves <paramref name="sourcePath"/> onto <paramref name="destinationPath"/>, replacing
        /// whatever is already there.
        /// A producer stages an external artifact beside its destination and moves it into place
        /// only once the Book mutation that names it has committed (ADR 0007).
        /// </summary>
        void MoveFile(string sourcePath, string destinationPath);
        Task WriteFileAsync(string path, Stream source);
        Task WriteAllLinesAsync(string path, IEnumerable<string> lines);
    }
}
