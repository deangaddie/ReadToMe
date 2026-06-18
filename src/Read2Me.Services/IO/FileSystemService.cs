using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Read2Me.Core.Configuration;
using Read2Me.Core.IO;

namespace Read2Me.Services.IO
{
    public class FileSystemService(IOptions<WorkspaceOptions> options) : IFileSystem
    {
        private readonly string _workspace = options.Value.FolderPath;

        public IReadOnlyList<string> ListProjectFolders()
        {
            if (!Directory.Exists(_workspace)) return [];
            return Directory.GetDirectories(_workspace)
                .Select(Path.GetFileName)
                .Where(n => n is not null)
                .Select(n => n!)
                .OrderBy(n => n)
                .ToList();
        }

        public bool ProjectFolderExists(string name) => Directory.Exists(Path.Combine(_workspace, name));

        public string GetProjectFolderPath(string name) => Path.Combine(_workspace, name);

        public void CreateProjectFolder(string name) => Directory.CreateDirectory(Path.Combine(_workspace, name));

        public void DeleteProjectFolder(string name) => Directory.Delete(Path.Combine(_workspace, name), recursive: true);

        public bool FileExists(string path) => File.Exists(path);

        public void EnsureDirectory(string path) => Directory.CreateDirectory(path);

        public void DeleteFile(string path) => File.Delete(path);

        public async Task WriteFileAsync(string path, Stream source)
        {
            using var dest = File.Create(path);
            await source.CopyToAsync(dest);
        }

        public Task WriteAllLinesAsync(string path, IEnumerable<string> lines) => File.WriteAllLinesAsync(path, lines);
    }
}
