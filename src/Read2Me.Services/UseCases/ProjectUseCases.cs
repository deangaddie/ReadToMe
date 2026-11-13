using Microsoft.Extensions.Logging;
using Read2Me.Core.Models;
using Read2Me.Data.Enums;
using Read2Me.Services.Mutations;
using Read2Me.Services.Series;

namespace Read2Me.Services.UseCases
{
    /// <summary>
    /// The project lifecycle (#330): create, update metadata and delete, with the rules that go with
    /// them. A delete forgets the book's Series membership (#206), and one title rule holds for
    /// create and update. Callers parse, resolve the folder and map the outcome.
    /// </summary>
    public class ProjectUseCases(
        IProjectCatalogReader reader, IProjectWriter writer, BookMutations mutations,
        SeriesRegistry registry, ILogger<ProjectUseCases> logger)
    {
        /// <summary>The refusal of a create with a blank title; the API also answers it for a missing file.</summary>
        public const string TitleAndFileRequired = "Fields 'title' and 'file' are required.";

        /// <summary>Creates a project from a book file. Title, book title and author are stored trimmed; a blank title is refused.</summary>
        public async Task<ProjectOutcome> CreateAsync(
            string title, string bookTitle, string author,
            string originalFileName, Stream fileStream, BookFileType fileType)
        {
            title = title.Trim();
            if (title.Length == 0) return new ProjectOutcome.Refused(TitleAndFileRequired);

            try
            {
                var folderName = await writer.CreateProjectAsync(
                    title, bookTitle.Trim(), author.Trim(), originalFileName, fileStream, fileType);
                return new ProjectOutcome.Created(folderName);
            }
            catch (ArgumentException ex) { return new ProjectOutcome.Failed(ex.Message); }
            catch (InvalidOperationException ex) { return new ProjectOutcome.Failed(ex.Message); }
            catch (IOException) { return new ProjectOutcome.Failed("Failed to save book file. Please try again."); }
            catch (Exception) { return new ProjectOutcome.Failed("Failed to create project. Please try again."); }
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

        /// <summary>
        /// Deletes the project folder, then forgets the book's Series membership by the Id read while
        /// the folder was still there. A folder that will not go is a failure and keeps the
        /// membership. A forget that throws is not: the project is gone, and the next write to that
        /// series heals the stale member, as it does for a book whose Id could not be read.
        /// </summary>
        public async Task<ProjectOutcome> DeleteAsync(ProjectFolderId folderId, CancellationToken ct = default)
        {
            var projectId = await ProjectIdAsync(folderId);

            try
            {
                writer.DeleteProject(folderId);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to delete project '{Folder}'", folderId);
                return new ProjectOutcome.Failed("Failed to delete project. Please try again.");
            }

            if (projectId is { } id)
            {
                try
                {
                    await registry.ForgetProjectAsync(id, ct);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex,
                        "Project '{Folder}' was deleted but its Series membership was not forgotten; the series' next write heals it",
                        folderId);
                }
            }
            return new ProjectOutcome.Done();
        }

        /// <summary>The project's Id, or null when its <c>project.db</c> cannot be read.</summary>
        private async Task<Guid?> ProjectIdAsync(ProjectFolderId folderId)
        {
            try
            {
                return (await reader.GetProjectAsync(folderId))?.Id;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not read the Id of project '{Folder}' before deleting it", folderId);
                return null;
            }
        }

        /// <summary>
        /// Title, book title and author, trimmed; null leaves a field alone. A provided-but-blank
        /// title or book title is a mistake, not a clear, and is refused; author may be blank.
        /// </summary>
        public async Task<ProjectOutcome> UpdateMetadataAsync(
            ProjectFolderId folderId, string? title, string? bookTitle, string? author)
        {
            title = title?.Trim();
            bookTitle = bookTitle?.Trim();
            author = author?.Trim();
            if (title is "") return new ProjectOutcome.Refused("Title cannot be blank.");
            if (bookTitle is "") return new ProjectOutcome.Refused("Book title cannot be blank.");

            try
            {
                await writer.UpdateMetadataAsync(folderId, title, bookTitle, author);
                return new ProjectOutcome.Done();
            }
            catch (Exception) { return new ProjectOutcome.Failed("Failed to save project details."); }
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

        /// <summary>
        /// Confirms, skips or (with a null state) reopens a Pipeline step through
        /// <see cref="BookMutations"/>, so the write lock serializes it with Book edits and a
        /// Checkpoints receipt refreshes every open Overview. The outcome comes back whole: the
        /// API answers a write-lock conflict differently from a refusal. Unlike its siblings this
        /// does not turn an unexpected fault into a refusal, which the API would send as a 422.
        /// </summary>
        public Task<BookMutationOutcome> SetCheckpointAsync(
            string folderName, PipelineStep step, CheckpointState? state) =>
            mutations.CommitAsync(new SetCheckpointMutation(new ProjectFolderId(folderName), step, state));
    }
}
