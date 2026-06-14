using System.IO;
using System.Threading.Tasks;
using Read2Me.Data.Enums;

namespace Read2Me.Services
{
    public interface IProjectWriter
    {
        Task<string> CreateProjectAsync(
            string title, string bookTitle, string author,
            string originalFileName, Stream fileStream, BookFileType fileType);
        Task SaveCoverImageAsync(string folderName, string filename, Stream stream);
        Task DeleteCoverImageAsync(string folderName);
        Task ClearBookContentAsync(string folderName);
        void DeleteProject(string folderName);
        Task SetParagraphItemCharacterAsync(string folderName, Guid itemId, Guid? characterId);
        Task DeleteVolumeAsync(string folderName, Guid volumeId);
        Task DeletePartAsync(string folderName, Guid partId);
        Task DeleteChapterAsync(string folderName, Guid chapterId);
        Task DeleteParagraphAsync(string folderName, Guid paragraphId);
        Task DeleteParagraphItemAsync(string folderName, Guid itemId);
    }
}
