namespace Read2Me.Core.IO
{
    /// <summary>A file on disk with the timestamp callers order by ("most recent first").</summary>
    public readonly record struct FileEntry(string Path, DateTime LastWriteTimeUtc);

    public interface IFileSystem
    {
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
        Task WriteFileAsync(string path, Stream source);
        Task WriteAllLinesAsync(string path, IEnumerable<string> lines);
    }
}
