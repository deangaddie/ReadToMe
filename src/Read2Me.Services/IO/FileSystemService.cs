using System.IO;
using System.Threading.Tasks;
using Read2Me.Core.IO;

namespace Read2Me.Services.IO
{
    public class FileSystemService : IFileSystem
    {
        public bool DirectoryExists(string path) => Directory.Exists(path);

        public bool FileExists(string path) => File.Exists(path);

        public string[] GetDirectories(string path) => Directory.GetDirectories(path);

        public void CreateDirectory(string path) => Directory.CreateDirectory(path);

        public void DeleteDirectory(string path, bool recursive) => Directory.Delete(path, recursive);

        public void DeleteFile(string path) => File.Delete(path);

        public async Task WriteFileAsync(string path, Stream source)
        {
            using var dest = File.Create(path);
            await source.CopyToAsync(dest);
        }
    }
}
