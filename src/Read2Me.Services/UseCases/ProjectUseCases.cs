using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Read2Me.Core.Exceptions;
using Read2Me.Core.Models;
using Read2Me.Data.Enums;
using Read2Me.Services.History;
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
        SeriesRegistry registry, ProjectActivity activity, ILogger<ProjectUseCases> logger)
    {
        /// <summary>The refusal of a create with a blank title; the API also answers it for a missing file.</summary>
        public const string TitleAndFileRequired = "Fields 'title' and 'file' are required.";

        /// <summary>
        /// Creates a project from a book file. Title, book title and author are stored trimmed; a
        /// blank title, or one with no folder name in it, is refused, and a title whose folder
        /// already exists is a conflict.
        /// </summary>
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
            catch (ProjectAlreadyExistsException ex) { return new ProjectOutcome.Conflict(ex.Message); }
            catch (ArgumentException ex) { return new ProjectOutcome.Refused(ex.Message); }
            catch (IOException ex)
            {
                logger.LogError(ex, "Failed to save the book file of new project '{Title}'", title);
                return new ProjectOutcome.Failed("Failed to save book file. Please try again.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Failed to create project '{Title}'", title);
                return new ProjectOutcome.Failed("Failed to create project. Please try again.");
            }
        }

        public async Task<Result<IReadOnlyList<ProjectSummary>>> GetSummariesAsync()
        {
            try
            {
                var summaries = await reader.GetProjectSummariesAsync();
                return Result<IReadOnlyList<ProjectSummary>>.Ok(summaries);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Failed to load the project summaries");
                return Result<IReadOnlyList<ProjectSummary>>.Fail("Failed to load projects.");
            }
        }

        /// <summary>
        /// Deletes the project folder, then forgets the book's Series membership by the Id read while
        /// the folder was still there. A folder that will not go is a failure and keeps the
        /// membership. A forget that throws is not: the project is gone, and the next write to that
        /// series heals the stale member, as it does for a book whose Id could not be read.
        /// <para>
        /// The whole of it holds the gate's deleting claim (#346), so nothing writes, enqueues or
        /// restores meanwhile. A busy or restoring project is refused, never cancelled.
        /// </para>
        /// </summary>
        public async Task<ProjectOutcome> DeleteAsync(ProjectFolderId folderId, CancellationToken ct = default)
        {
            if (activity.TryBeginDelete(folderId) is { } refusal)
                return new ProjectOutcome.ActivityRefused(refusal);
            try
            {
                return await DeleteClaimedAsync(folderId, ct);
            }
            finally
            {
                activity.EndDelete(folderId);
            }
        }

        private async Task<ProjectOutcome> DeleteClaimedAsync(ProjectFolderId folderId, CancellationToken ct)
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

            return await WriteAsync(folderId, "Failed to save project details.",
                () => writer.UpdateMetadataAsync(folderId, title, bookTitle, author));
        }

        public Task<ProjectOutcome> SaveCoverImageAsync(ProjectFolderId folderId, string filename, Stream stream) =>
            WriteAsync(folderId, "Failed to save cover image.", () => writer.SaveCoverImageAsync(folderId, filename, stream));

        public Task<ProjectOutcome> DeleteCoverImageAsync(ProjectFolderId folderId) =>
            WriteAsync(folderId, "Failed to delete cover image.", () => writer.DeleteCoverImageAsync(folderId));

        /// <summary>
        /// Flips the Book-wide narrator-only policy through <see cref="BookMutations"/>, so
        /// every open Book View reconciles the audio eligibility, denominators and Audio Item
        /// Selection the flip moves — without anyone navigating away and back (ADR 0007).
        /// <para>
        /// A flip to the value already stored is a success that changed nothing, which is exactly
        /// what the switch in front of the user is already showing.
        /// </para>
        /// </summary>
        public Task<ProjectOutcome> SetNarratorOnlyModeAsync(ProjectFolderId folderId, bool value) =>
            WriteAsync(folderId, "Failed to save narrator-only setting.",
                () => mutations.CommitAsync(new SetNarratorOnlyModeMutation(folderId, value)));

        /// <summary>
        /// One Book write, its outcome mapped for the reader. A fault nobody foresaw is logged with the
        /// folder and answered with <paramref name="failure"/>; a cancellation propagates unlogged.
        /// </summary>
        private async Task<ProjectOutcome> WriteAsync(
            ProjectFolderId folderId, string failure, Func<Task<BookMutationOutcome>> write)
        {
            BookMutationOutcome outcome;
            try
            {
                outcome = await write();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "{Failure} Project '{Folder}'", failure, folderId);
                return new ProjectOutcome.Failed(failure);
            }
            return Outcome(outcome);
        }

        /// <summary>A rejection is an expected answer with the mutation's own reason; a cancellation is rethrown.</summary>
        private static ProjectOutcome Outcome(BookMutationOutcome outcome) => outcome switch
        {
            BookMutationOutcome.Committed or BookMutationOutcome.NoChange => new ProjectOutcome.Done(),
            BookMutationOutcome.Rejected
            {
                Reason: BookMutationRejection.Conflict or BookMutationRejection.Stale
                    or BookMutationRejection.Restoring or BookMutationRejection.Blocked,
            } rejected => new ProjectOutcome.Conflict(rejected.Message),
            BookMutationOutcome.Rejected { Reason: BookMutationRejection.NotFound } rejected => new ProjectOutcome.NotFound(rejected.Message),
            BookMutationOutcome.Rejected { Reason: BookMutationRejection.Validation } rejected => new ProjectOutcome.Refused(rejected.Message),
            BookMutationOutcome.Rejected { Reason: BookMutationRejection.Cancelled } rejected => throw new OperationCanceledException(rejected.Message),
            _ => throw new UnreachableException($"A project write answered {outcome}."),
        };

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
