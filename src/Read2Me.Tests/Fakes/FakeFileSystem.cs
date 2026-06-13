using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Read2Me.Core.IO;

namespace Read2Me.Tests.Fakes
{
    public class FakeFileSystem : IFileSystem
    {
        private const string FakeRoot = "C:\\fake-workspace";

        private readonly HashSet<string> _folders = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, byte[]> _files = new(StringComparer.OrdinalIgnoreCase);

        public void SeedFolder(params string[] names)
        {
            foreach (var n in names)
                _folders.Add(n);
        }

        public IReadOnlyList<string> ListProjectFolders() =>
            _folders.OrderBy(n => n).ToList();

        public bool ProjectFolderExists(string name) => _folders.Contains(name);

        public string GetProjectFolderPath(string name) => Path.Combine(FakeRoot, name);

        public void CreateProjectFolder(string name) => _folders.Add(name);

        public void DeleteProjectFolder(string name)
        {
            _folders.Remove(name);
            var prefix = GetProjectFolderPath(name) + Path.DirectorySeparatorChar;
            foreach (var f in _files.Keys.Where(f => f.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList())
                _files.Remove(f);
        }

        public bool FileExists(string path) => _files.ContainsKey(path);

        public void DeleteFile(string path) => _files.Remove(path);

        public async Task WriteFileAsync(string path, Stream source)
        {
            using var ms = new MemoryStream();
            await source.CopyToAsync(ms);
            _files[path] = ms.ToArray();
        }

        public byte[] GetFileContent(string path) => _files[path];
    }
}
