using Read2Me.Core.Models;
using Read2Me.Data.Enums;

namespace Read2Me.Services
{
    public interface IProjectWriter
    {
        Task<string> CreateProjectAsync(
            string title, string bookTitle, string author,
            string originalFileName, Stream fileStream, BookFileType fileType);
        /// <summary>
        /// Overwrites the editable metadata: a null argument leaves that field as it is. The folder
        /// name never changes — it was derived from the title at creation and is the project's id.
        /// </summary>
        Task UpdateMetadataAsync(ProjectFolderId folderId, string? title, string? bookTitle, string? author);
        Task SaveCoverImageAsync(ProjectFolderId folderId, string filename, Stream stream);
        Task DeleteCoverImageAsync(ProjectFolderId folderId);
        void DeleteProject(ProjectFolderId folderId);
    }
}
