using Read2Me.Core.Models;
using Read2Me.Data.Enums;
using Read2Me.Services.Mutations;

namespace Read2Me.Services.UseCases
{
    public class ProjectUseCases(
        IProjectCatalogReader reader, IProjectWriter writer, BookMutations mutations)
    {
        public async Task<Result<string>> CreateAsync(
            string title, string bookTitle, string author,
            string originalFileName, Stream fileStream, BookFileType fileType)
        {
            try
            {
                var folderName = await writer.CreateProjectAsync(title, bookTitle, author, originalFileName, fileStream, fileType);
                return Result<string>.Ok(folderName);
            }
            catch (ArgumentException ex) { return Result<string>.Fail(ex.Message); }
            catch (InvalidOperationException ex) { return Result<string>.Fail(ex.Message); }
            catch (IOException) { return Result<string>.Fail("Failed to save book file. Please try again."); }
            catch (Exception) { return Result<string>.Fail("Failed to create project. Please try again."); }
        }

        public async Task<Result<IReadOnlyList<ProjectSummary>>> GetSummariesAsync()
        {
            try
            {
                var summaries = await reader.GetProjectSummariesAsync();
                return Result<IReadOnlyList<ProjectSummary>>.Ok(summaries);
            }
            catch (Exception) { return Result<IReadOnlyList<ProjectSummary>>.Fail("Failed to load projects."); }
        }

        public Result DeleteProject(string folderName)
        {
            try
            {
                writer.DeleteProject(folderName);
                return Result.Ok();
            }
            catch (Exception) { return Result.Fail("Failed to delete project. Please try again."); }
        }

        public async Task<Result> SaveCoverImageAsync(string folderName, string filename, Stream stream)
        {
            try
            {
                await writer.SaveCoverImageAsync(folderName, filename, stream);
                return Result.Ok();
            }
            catch (Exception) { return Result.Fail("Failed to save cover image."); }
        }

        public async Task<Result> DeleteCoverImageAsync(string folderName)
        {
            try
            {
                await writer.DeleteCoverImageAsync(folderName);
                return Result.Ok();
            }
            catch (Exception) { return Result.Fail("Failed to delete cover image."); }
        }

        /// <summary>
        /// Flips the Book-wide narrator-only policy through <see cref="BookMutations"/>, so
        /// every open Book View reconciles the audio eligibility, denominators and Audio Item
        /// Selection the flip moves — without anyone navigating away and back (ADR 0007).
        /// <para>
        /// A flip to the value already stored is a success that changed nothing, which is exactly
        /// what the switch in front of the user is already showing.
        /// </para>
        /// </summary>
        public async Task<Result> SetNarratorOnlyModeAsync(string folderName, bool value)
        {
            try
            {
                var outcome = await mutations.CommitAsync(
                    new SetNarratorOnlyModeMutation(new ProjectFolderId(folderName), value));

                return outcome is BookMutationOutcome.Rejected rejected
                    ? Result.Fail(rejected.Message)
                    : Result.Ok();
            }
            catch (Exception) { return Result.Fail("Failed to save narrator-only setting."); }
        }
    }
}
