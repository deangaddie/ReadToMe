using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Read2Me.Core.IO;
using Read2Me.Core.Models;
using Read2Me.Services.Mutations;

namespace Read2Me.Services.UseCases
{
    /// <summary>
    /// Where an import's mutation is committed. The API endpoint hands over
    /// <see cref="BookMutations.CommitAsync"/> directly; a Blazor circuit hands over its own Book
    /// View projection, so the circuit that asked for the reread is coherent before the gesture
    /// returns and is never told its own change happened "elsewhere" (ADR 0007).
    /// </summary>
    public delegate Task<BookMutationOutcome> CommitBookMutation(BookMutation mutation, CancellationToken ct);

    /// <summary>
    /// The import producer: it reads a project's source file, stages the cover image it extracts,
    /// and commits the whole replacement as one Book mutation.
    /// <para>
    /// The ordering is the point. Reading a file is slow and fails in ordinary ways, so it happens
    /// before any transaction is open and before any write lock is taken. The extracted cover is
    /// written to disk before the mutation names it, and removed again if the mutation does not
    /// commit. And the removal of the old content and the writing of the new are one commit, so no
    /// open Book View ever renders the empty Book in between (ADR 0007).
    /// </para>
    /// </summary>
    public class BookUseCases(
        BookReadingService bookReadingService,
        BookMutations mutations,
        ProjectDbSession session,
        IFileSystem fs,
        ILogger<BookUseCases> logger)
    {
        /// <summary>Imports through <see cref="BookMutations"/> itself — for callers with no Book View.</summary>
        public virtual Task<BookImportOutcome> ImportAsync(
            ProjectFolderId folderId, bool reread = false, CancellationToken cancellationToken = default) =>
            ImportAsync(folderId, reread, mutations.CommitAsync, cancellationToken);

        public virtual Task<BookImportOutcome> ImportAsync(
            ProjectFolderId folderId, bool reread, CommitBookMutation commit,
            CancellationToken cancellationToken = default) =>
            ReplaceContentAsync(
                folderId,
                async ct =>
                {
                    var read = await bookReadingService.ReadBookAsync(folderId, ct);
                    return (read.Content, read.CoverImage, reread);
                },
                commit,
                cancellationToken);

        /// <summary>Imports through <see cref="BookMutations"/> itself — for callers with no Book View.</summary>
        public virtual Task<BookImportOutcome> ImportManuallyAsync(
            ProjectFolderId folderId, ManualReadOptions options, CancellationToken cancellationToken = default) =>
            ImportManuallyAsync(folderId, options, mutations.CommitAsync, cancellationToken);

        public virtual Task<BookImportOutcome> ImportManuallyAsync(
            ProjectFolderId folderId, ManualReadOptions options, CommitBookMutation commit,
            CancellationToken cancellationToken = default) =>
            ReplaceContentAsync(
                folderId,
                async ct =>
                {
                    // Flattened from the source file rather than from the Book, so a manual reread
                    // re-splits the original text and not the last split of it.
                    var lines = await bookReadingService.FlattenFromFileAsync(folderId, ct);
                    return (bookReadingService.ReadManually(lines, options), (byte[]?)null, true);
                },
                commit,
                cancellationToken);

        /// <summary>
        /// The shape all three imports share: read outside the transaction, stage the cover, commit
        /// once, and tidy up whatever the commit did not take.
        /// </summary>
        private async Task<BookImportOutcome> ReplaceContentAsync(
            ProjectFolderId folderId,
            Func<CancellationToken, Task<(BookContent Content, byte[]? CoverImage, bool ReplaceExisting)>> read,
            CommitBookMutation commit,
            CancellationToken cancellationToken)
        {
            BookContent content;
            byte[]? coverImage;
            bool replaceExisting;
            try
            {
                (content, coverImage, replaceExisting) = await read(cancellationToken);
            }
            catch (FileNotFoundException ex)
            {
                return new BookImportOutcome.Failed(BookImportFailure.FileMissing, ex.Message);
            }
            catch (OperationCanceledException)
            {
                return Cancelled();
            }
            // No project record, and a project whose file type nothing can read: both are things
            // about this project that no retry will change, so neither is an "unexpected" failure.
            catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
            {
                return new BookImportOutcome.Failed(BookImportFailure.Invalid, ex.Message);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Reading the source file of '{Folder}' failed.", folderId.Value);
                return Unexpected();
            }

            var stagedCover = await TryStageCoverAsync(folderId, coverImage, cancellationToken);

            BookMutationOutcome outcome;
            try
            {
                outcome = await commit(
                    new ImportBookContentMutation(folderId, content, replaceExisting, stagedCover),
                    cancellationToken);
            }
            catch (Exception ex)
            {
                // A throw says nothing about whether the transaction committed — a mutation that
                // commits and then fails to reconcile the caller's own view throws from past the
                // commit point. So the staged cover stays: a file the Book may now name is safe,
                // and deleting one it does name is not. An orphan is the cheaper mistake.
                logger.LogWarning(ex, "Replacing the content of '{Folder}' failed.", folderId.Value);
                return ex is OperationCanceledException ? Cancelled() : Unexpected();
            }

            if (outcome is BookMutationOutcome.Committed committed)
            {
                // The transaction rechecks the cover for itself, and can decline one this read saw
                // room for. It says so on the receipt — the cover is the only thing this mutation
                // reports ProjectPolicy for — so the file it declined is still this adapter's to
                // clear away.
                if (!committed.Receipt.Effects.Facets.HasFlag(BookFacets.ProjectPolicy))
                    DiscardStagedCover(folderId, stagedCover);

                return new BookImportOutcome.Replaced();
            }

            // Committed is the only outcome that named the staged cover, so every other one — each of
            // which reports honestly that nothing committed — leaves a file nothing points at.
            DiscardStagedCover(folderId, stagedCover);

            return outcome switch
            {
                BookMutationOutcome.NoChange => new BookImportOutcome.Unchanged(),
                BookMutationOutcome.Rejected { Reason: BookMutationRejection.Cancelled } => Cancelled(),
                BookMutationOutcome.Rejected
                    { Reason: BookMutationRejection.Conflict or BookMutationRejection.Stale } conflict =>
                    new BookImportOutcome.Failed(BookImportFailure.Conflict, conflict.Message),
                BookMutationOutcome.Rejected rejected =>
                    new BookImportOutcome.Failed(BookImportFailure.Invalid, rejected.Message),
                _ => Unexpected(),
            };
        }

        private static BookImportOutcome Cancelled() =>
            new BookImportOutcome.Failed(BookImportFailure.Cancelled, "The import was cancelled.");

        private static BookImportOutcome Unexpected() =>
            new BookImportOutcome.Failed(BookImportFailure.Unexpected, "Failed to import book. Please try again.");

        /// <summary>
        /// Writes an extracted cover into the project folder and returns the name the mutation should
        /// record — or null when there is nothing to record.
        /// <para>
        /// Best-effort throughout: a cover that cannot be written is not a reason to refuse a Book.
        /// A project that already has one is left alone, because a cover the reader chose outranks
        /// whatever the epub carries.
        /// </para>
        /// </summary>
        private async Task<string?> TryStageCoverAsync(
            ProjectFolderId folderId, byte[]? coverImage, CancellationToken cancellationToken)
        {
            if (coverImage is null || coverImage.Length == 0)
                return null;

            try
            {
                var db = await session.OpenAsync(folderId);
                var entity = await db.Projects.SingleOrDefaultAsync(cancellationToken);
                if (entity?.CoverImage != null)
                    return null;

                if (DetectImageExtension(coverImage) is not { } ext)
                    return null;

                var filename = "cover" + ext;
                await fs.WriteFileAsync(fs.ProjectFilePath(folderId, filename), new MemoryStream(coverImage));
                return filename;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Staging the cover image of '{Folder}' failed.", folderId.Value);
                return null;
            }
        }

        /// <summary>Removes a staged cover the Book never came to name. Best-effort, for the same reason.</summary>
        private void DiscardStagedCover(ProjectFolderId folderId, string? filename)
        {
            if (filename is null) return;

            try
            {
                fs.DeleteProjectFile(folderId, filename);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Removing the staged cover image of '{Folder}' failed.", folderId.Value);
            }
        }

        private static string? DetectImageExtension(byte[] bytes)
        {
            if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
                return ".jpg";
            if (bytes.Length >= 4 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
                return ".png";
            return null;
        }
    }
}
