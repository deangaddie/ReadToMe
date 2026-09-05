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
        void DeleteProject(ProjectFolderId folderId);
    }
}
