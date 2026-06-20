using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Read2Me.Core.IO;

namespace Read2Me.Tests.Fakes
{
    public class FakeFileSystem : IFileSystem
    {
        private readonly string _root;
        private readonly HashSet<string> _folders = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, byte[]> _files = new(StringComparer.OrdinalIgnoreCase);

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

        public IReadOnlyList<string> ListProjectFolders() =>
            _folders.OrderBy(n => n).ToList();

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
