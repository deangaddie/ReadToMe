using System.IO;
using System.Threading.Tasks;
using Read2Me.Core.Models;
using Read2Me.Data.Enums;

namespace Read2Me.Services
{
    public interface IProjectWriter
    {
        Task<string> CreateProjectAsync(
            string title, string bookTitle, string author,
            string originalFileName, Stream fileStream, BookFileType fileType);
        Task SaveCoverImageAsync(ProjectFolderId folderId, string filename, Stream stream);
        Task DeleteCoverImageAsync(ProjectFolderId folderId);
        Task SetNarratorOnlyModeAsync(ProjectFolderId folderId, bool value);
        void DeleteProject(ProjectFolderId folderId);
    }
}
