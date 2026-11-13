using Read2Me.Core.Models;
using Read2Me.Data.Enums;
using Read2Me.Services.Mutations;

namespace Read2Me.Services
{
    public interface IProjectWriter
    {
        /// <summary>
        /// Creates the project folder, its book file and its first row; returns the folder name.
        /// Throws <see cref="Core.Exceptions.ProjectAlreadyExistsException"/> for a title whose
        /// folder exists, and <see cref="ArgumentException"/> for one with no folder name at all.
        /// </summary>
        Task<string> CreateProjectAsync(
            string title, string bookTitle, string author,
            string originalFileName, Stream fileStream, BookFileType fileType);
        /// <summary>
        /// Overwrites the editable metadata: a null argument leaves that field as it is. The folder
        /// name never changes — it was derived from the title at creation and is the project's id.
        /// A rejection comes back as the outcome; only an unforeseen fault throws.
        /// </summary>
        Task<BookMutationOutcome> UpdateMetadataAsync(ProjectFolderId folderId, string? title, string? bookTitle, string? author);
        /// <summary>Stores the image and names it the cover; a rejected mutation puts the old cover back and comes back as the outcome.</summary>
        Task<BookMutationOutcome> SaveCoverImageAsync(ProjectFolderId folderId, string filename, Stream stream);
        /// <summary>Clears the cover; a project without one is <see cref="BookMutationOutcome.NoChange"/>.</summary>
        Task<BookMutationOutcome> DeleteCoverImageAsync(ProjectFolderId folderId);
        void DeleteProject(ProjectFolderId folderId);
    }
}
