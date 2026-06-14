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
        Task UpdateVolumeTitleAsync(string folderName, Guid volumeId, string title);
        Task UpdatePartTitleAsync(string folderName, Guid partId, string title);
        Task UpdateChapterTitleAsync(string folderName, Guid chapterId, string title);
        Task UpdateParagraphItemTextAsync(string folderName, Guid itemId, string text);
        Task SplitVolumeAsync(string folderName, Guid partId, string? newTitle);
        Task SplitPartAsync(string folderName, Guid chapterId, string? newTitle);
        Task SplitChapterAsync(string folderName, Guid paragraphId, string? newTitle);
        Task SplitParagraphAsync(string folderName, Guid itemId, string? newTitle);
        Task SplitParagraphItemAsync(string folderName, Guid itemId);
        Task AddBookTitleAsync(string folderName);
        Task AddVolumeTitlesAsync(string folderName);
        Task AddPartTitlesAsync(string folderName);
        Task AddChapterTitlesAsync(string folderName);
        Task MergeVolumeWithPreviousAsync(string folderName, Guid volumeId);
        Task MergeVolumeWithNextAsync(string folderName, Guid volumeId);
        Task MergePartWithPreviousAsync(string folderName, Guid partId);
        Task MergePartWithNextAsync(string folderName, Guid partId);
        Task MergeChapterWithPreviousAsync(string folderName, Guid chapterId);
        Task MergeChapterWithNextAsync(string folderName, Guid chapterId);
        Task MergeParagraphWithPreviousAsync(string folderName, Guid paragraphId);
        Task MergeParagraphWithNextAsync(string folderName, Guid paragraphId);
        Task MergeParagraphItemWithPreviousAsync(string folderName, Guid itemId);
        Task MergeParagraphItemWithNextAsync(string folderName, Guid itemId);
    }
}
