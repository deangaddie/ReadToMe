using System.IO;
using System.Threading.Tasks;
using System.Collections.Generic;

namespace Read2Me.Core.IO
{
    public interface IFileSystem
    {
        IReadOnlyList<string> ListProjectFolders();
        bool ProjectFolderExists(string name);
        string GetProjectFolderPath(string name);
        void CreateProjectFolder(string name);
        void DeleteProjectFolder(string name);

        bool FileExists(string path);
        void EnsureDirectory(string path);
        void DeleteFile(string path);
        Task WriteFileAsync(string path, Stream source);
        Task WriteAllLinesAsync(string path, IEnumerable<string> lines);
    }
}
