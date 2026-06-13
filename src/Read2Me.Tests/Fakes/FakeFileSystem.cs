using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Read2Me.Core.IO;

namespace Read2Me.Tests.Fakes
{
    public class FakeFileSystem : IFileSystem
    {
        private readonly HashSet<string> _directories = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, byte[]> _files = new(StringComparer.OrdinalIgnoreCase);

        public void Seed(params string[] paths)
        {
            foreach (var p in paths)
                _directories.Add(p);
        }

        public bool DirectoryExists(string path) => _directories.Contains(path);

        public bool FileExists(string path) => _files.ContainsKey(path);

        public string[] GetDirectories(string path)
        {
            return _directories
                .Where(d => string.Equals(
                    Path.GetDirectoryName(d), path, StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }

        public void CreateDirectory(string path) => _directories.Add(path);

        public void DeleteDirectory(string path, bool recursive)
        {
            if (recursive)
            {
                var prefix = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    + Path.DirectorySeparatorChar;
                var dirsToRemove = _directories
                    .Where(d => d.Equals(path, StringComparison.OrdinalIgnoreCase)
                             || d.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                foreach (var d in dirsToRemove) _directories.Remove(d);

                var filesToRemove = _files.Keys
                    .Where(f => f.Equals(path, StringComparison.OrdinalIgnoreCase)
                             || f.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                foreach (var f in filesToRemove) _files.Remove(f);
            }
            else
            {
                _directories.Remove(path);
            }
        }

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
