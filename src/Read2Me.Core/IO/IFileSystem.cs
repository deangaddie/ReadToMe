using System.IO;
using System.Threading.Tasks;

namespace Read2Me.Core.IO
{
    public interface IFileSystem
    {
        bool DirectoryExists(string path);
        bool FileExists(string path);
        string[] GetDirectories(string path);
        void CreateDirectory(string path);
        void DeleteDirectory(string path, bool recursive);
        void DeleteFile(string path);
        Task WriteFileAsync(string path, Stream source);
    }
}
