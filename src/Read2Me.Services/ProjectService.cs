using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Read2Me.Core.Exceptions;
using Read2Me.Core.IO;
using Read2Me.Core.Models;
using Read2Me.Data.Enums;
using Read2Me.Services.Mutations;
using ProjectEntity = Read2Me.Data.Entities.Project;

namespace Read2Me.Services
{
    public class ProjectService : IProjectWriter, IAsyncDisposable
    {
        private readonly IFileSystem _fs;
        private readonly ProjectDbSession _session;
        private readonly BookMutations _mutations;
        private readonly ILogger<ProjectService> _logger;

        public ProjectService(
            IFileSystem fs, ProjectDbSession session, BookMutations mutations, ILogger<ProjectService> logger)
        {
            _fs = fs;
            _session = session;
            _mutations = mutations;
            _logger = logger;
        }

        public async ValueTask DisposeAsync()
        {
            await _session.DisposeAsync();
        }

        public string SanitizeName(string name) => Core.Models.NameSanitizer.Sanitize(name);

        public bool CreateProject(string name)
        {
            var sanitized = SanitizeName(name);
            if (string.IsNullOrEmpty(sanitized))
            {
                _logger.LogWarning("CreateProject: name '{Name}' produced an empty sanitized folder name", name);
                return false;
            }

            if (_fs.ProjectFolderExists(sanitized))
            {
                _logger.LogWarning("CreateProject: folder already exists for '{Name}'", sanitized);
                return false;
            }

            _fs.CreateProjectFolder(sanitized);
            _logger.LogInformation("Created project folder: {Name}", sanitized);
            return true;
        }

        public async Task<string> CreateProjectAsync(
            string title, string bookTitle, string author,
            string originalFileName, Stream fileStream, BookFileType fileType)
        {
            var folderId = SanitizeName(title);
            if (string.IsNullOrEmpty(folderId))
                throw new ArgumentException("Title produces an empty folder name.", nameof(title));

            if (_fs.ProjectFolderExists(folderId))
                throw new ProjectAlreadyExistsException(folderId);

            _logger.LogInformation("Creating project '{Title}' in folder '{Folder}'", title, folderId);

            _fs.CreateProjectFolder(folderId);
            try
            {
                var destFile = Path.Combine(_fs.GetProjectFolderPath(folderId), originalFileName);
                await _fs.WriteFileAsync(destFile, fileStream);
                _logger.LogDebug("Saved book file: {File}", destFile);

                var db = await _session.OpenAsync(folderId);

                // A system write, and the one this class keeps: the project's first row, before there
                // is a Book to mutate. Every later change to it is a Book mutation.
                db.Projects.Add(new ProjectEntity
                {
                    Title = title,
                    BookTitle = bookTitle,
                    Author = author,
                    Filename = originalFileName,
                    Type = fileType,
                });
                await db.SaveChangesAsync();

                _logger.LogInformation("Project '{Title}' created successfully", title);
                return folderId;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create project '{Title}'. Rolling back file system changes.", title);
                try
                {
                    _fs.DeleteProjectFolder(folderId);
                }
                catch (Exception rollbackEx)
                {
                    _logger.LogError(rollbackEx, "Failed to rollback project folder deletion for '{FolderId}'", folderId);
                }
                throw;
            }
        }

        public async Task UpdateMetadataAsync(ProjectFolderId folderId, string? title, string? bookTitle, string? author)
        {
            var outcome = await _mutations.CommitAsync(
                new UpdateProjectMetadataMutation(folderId, title, bookTitle, author));
            ThrowIfRejected(outcome, folderId);
            _logger.LogInformation("Metadata updated for project '{Folder}'", folderId);
        }

        /// <summary>
        /// The image arrives before the mutation that names it, and the one it replaces leaves after
        /// (ADR 0007), so the project never names a cover that is not on disk.
        /// </summary>
        public async Task SaveCoverImageAsync(ProjectFolderId folderId, string filename, Stream stream)
        {
            _logger.LogInformation("Saving cover image '{File}' for project '{Folder}'", filename, folderId);
            var previous = await CurrentCoverAsync(folderId);
            // An upload under the cover's own name overwrites it, so keep its bytes to put back.
            var overwritten = filename == previous ? await ReadCoverAsync(folderId, filename) : null;

            await _fs.WriteFileAsync(CoverPath(folderId, filename), stream);

            var outcome = await _mutations.CommitAsync(new SetCoverImageMutation(folderId, filename));
            if (outcome is BookMutationOutcome.Committed)
            {
                if (previous is not null && previous != filename)
                    DeleteCoverFile(folderId, previous);
                _logger.LogInformation("Cover image saved for project '{Folder}'", folderId);
                return;
            }

            // The project still names the previous cover: a file it never came to name goes again,
            // and a cover the upload overwrote gets its own content back.
            if (overwritten is not null)
                await _fs.WriteFileAsync(CoverPath(folderId, filename), new MemoryStream(overwritten));
            else if (filename != previous)
                DeleteCoverFile(folderId, filename);
            ThrowIfRejected(outcome, folderId);
        }

        public async Task DeleteCoverImageAsync(ProjectFolderId folderId)
        {
            _logger.LogInformation("Deleting cover image for project '{Folder}'", folderId);
            var previous = await CurrentCoverAsync(folderId);
            if (previous is null)
            {
                _logger.LogDebug("DeleteCoverImageAsync: no cover image set for '{Folder}'", folderId);
                return;
            }

            // The row goes first and the file only once that commits: a cover the project still
            // names must still be there.
            var outcome = await _mutations.CommitAsync(new SetCoverImageMutation(folderId, null));
            ThrowIfRejected(outcome, folderId);
            if (outcome is BookMutationOutcome.Committed)
                DeleteCoverFile(folderId, previous);
            _logger.LogInformation("Cover image removed for project '{Folder}'", folderId);
        }

        public void DeleteProject(ProjectFolderId folderId)
        {
            _session.Evict(folderId);

            if (_fs.ProjectFolderExists(folderId))
            {
                _logger.LogInformation("Deleting project '{Folder}'", folderId);
                _fs.DeleteProjectFolder(folderId);
                _logger.LogInformation("Project '{Folder}' deleted", folderId);
            }
            else
            {
                _logger.LogWarning("DeleteProject: folder not found '{Folder}'", folderId);
            }
        }

        /// <summary>The cover the project names now, read untracked so the mutation cannot change it under us.</summary>
        private async Task<string?> CurrentCoverAsync(ProjectFolderId folderId)
        {
            var db = await _session.OpenAsync(folderId);
            var entity = await db.Projects.AsNoTracking().SingleOrDefaultAsync()
                ?? throw new ProjectNotFoundException(folderId.Value);
            return entity.CoverImage;
        }

        private async Task<byte[]?> ReadCoverAsync(ProjectFolderId folderId, string filename)
        {
            var path = CoverPath(folderId, filename);
            if (!_fs.FileExists(path))
                return null;

            using var source = _fs.OpenRead(path);
            using var buffer = new MemoryStream();
            await source.CopyToAsync(buffer);
            return buffer.ToArray();
        }

        private string CoverPath(ProjectFolderId folderId, string filename) =>
            Path.Combine(_fs.GetProjectFolderPath(folderId), filename);

        private void DeleteCoverFile(ProjectFolderId folderId, string filename)
        {
            var path = CoverPath(folderId, filename);
            if (!_fs.FileExists(path))
                return;

            _fs.DeleteFile(path);
            _logger.LogDebug("Deleted cover image file: {File}", path);
        }

        /// <summary>A committed or no-change outcome returns; a rejection throws what this writer always threw.</summary>
        private static void ThrowIfRejected(BookMutationOutcome outcome, ProjectFolderId folderId)
        {
            switch (outcome)
            {
                case BookMutationOutcome.Rejected { Reason: BookMutationRejection.NotFound }:
                    throw new ProjectNotFoundException(folderId.Value);
                case BookMutationOutcome.Rejected { Reason: BookMutationRejection.Cancelled } rejected:
                    throw new OperationCanceledException(rejected.Message);
                case BookMutationOutcome.Rejected rejected:
                    throw new InvalidOperationException(rejected.Message);
            }
        }
    }
}
