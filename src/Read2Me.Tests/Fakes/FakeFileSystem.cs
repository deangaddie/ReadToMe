using Read2Me.Core.IO;

namespace Read2Me.Tests.Fakes
{
    public class FakeFileSystem : IFileSystem
    {
        private readonly string _root;
        private readonly HashSet<string> _folders = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, byte[]> _files = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, DateTime> _writeTimes = new(StringComparer.OrdinalIgnoreCase);

        public FakeFileSystem(string root = "C:\\fake-workspace")
        {
            _root = root;
        }

        public void SeedFolder(params string[] names)
        {
            foreach (var n in names)
                _folders.Add(n);
        }

        public void SeedFile(string path, byte[] content) => _files[path] = content;

        public void SeedFile(string path, byte[] content, DateTime lastWriteTimeUtc)
        {
            _files[path] = content;
            _writeTimes[path] = lastWriteTimeUtc;
        }

        public IReadOnlyList<string> ListProjectFolders() =>
            _folders.OrderBy(n => n).ToList();

        public IReadOnlyList<FileEntry> ListFiles(string directoryPath, string searchPattern)
        {
            var prefix = directoryPath.TrimEnd('/', '\\') + Path.DirectorySeparatorChar;
            var regex = new System.Text.RegularExpressions.Regex(
                "^" + System.Text.RegularExpressions.Regex.Escape(searchPattern).Replace("\\*", ".*").Replace("\\?", ".") + "$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            return _files.Keys
                .Where(p => p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .Where(p => !p[prefix.Length..].Contains(Path.DirectorySeparatorChar))
                .Where(p => regex.IsMatch(Path.GetFileName(p)))
                .Select(p => new FileEntry(p, _writeTimes.TryGetValue(p, out var t) ? t : DateTime.UnixEpoch))
                .ToList();
        }

        public bool ProjectFolderExists(string name) => _folders.Contains(name);

        public string GetProjectFolderPath(string name) => Path.Combine(_root, name);

        public void CreateProjectFolder(string name) => _folders.Add(name);

        public void DeleteProjectFolder(string name)
        {
            _folders.Remove(name);
            var prefix = GetProjectFolderPath(name) + Path.DirectorySeparatorChar;
            foreach (var f in _files.Keys.Where(f => f.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList())
                _files.Remove(f);
        }

        public bool FileExists(string path) => _files.ContainsKey(path);

        public Stream OpenRead(string path)
        {
            if (_files.TryGetValue(path, out var data)) return new MemoryStream(data);
            throw new FileNotFoundException($"File not found in FakeFileSystem: {path}");
        }

        public void EnsureDirectory(string path) { }

        public void DeleteFile(string path) => _files.Remove(path);

        public async Task WriteFileAsync(string path, Stream source)
        {
            using var ms = new MemoryStream();
            await source.CopyToAsync(ms);
            _files[path] = ms.ToArray();
        }

        public Task WriteAllLinesAsync(string path, IEnumerable<string> lines)
        {
            _files[path] = System.Text.Encoding.UTF8.GetBytes(string.Join(Environment.NewLine, lines) + Environment.NewLine);
            return Task.CompletedTask;
        }

        public byte[] GetFileContent(string path) => _files[path];

        public IReadOnlyList<string> GetAllPaths() => _files.Keys.ToList();
    }
}
