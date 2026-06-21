using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Read2Me.Core.Exceptions;
using Read2Me.Core.IO;
using Read2Me.Core.Models;
using Read2Me.Data.Enums;
using ProjectEntity = Read2Me.Data.Entities.Project;

namespace Read2Me.Services
{
    public class ProjectService : IProjectWriter, IAsyncDisposable
    {
        private readonly IFileSystem _fs;
        private readonly ProjectDbSession _session;
        private readonly ILogger<ProjectService> _logger;

        public ProjectService(IFileSystem fs, ProjectDbSession session, ILogger<ProjectService> logger)
        {
            _fs = fs;
            _session = session;
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

        public async Task SaveCoverImageAsync(ProjectFolderId folderId, string filename, Stream stream)
        {
            _logger.LogInformation("Saving cover image '{File}' for project '{Folder}'", filename, folderId);
            var db = await _session.OpenAsync(folderId);
            var entity = await db.Projects.SingleOrDefaultAsync();
            if (entity == null)
                throw new ProjectNotFoundException(folderId.Value);

            var folderPath = _fs.GetProjectFolderPath(folderId);
            if (entity.CoverImage != null)
            {
                var existing = Path.Combine(folderPath, entity.CoverImage);
                if (_fs.FileExists(existing))
                {
                    _fs.DeleteFile(existing);
                    _logger.LogDebug("Deleted previous cover image: {File}", existing);
                }
            }

            await _fs.WriteFileAsync(Path.Combine(folderPath, filename), stream);
            entity.CoverImage = filename;
            await db.SaveChangesAsync();
            _logger.LogInformation("Cover image saved for project '{Folder}'", folderId);
        }

        public async Task DeleteCoverImageAsync(ProjectFolderId folderId)
        {
            _logger.LogInformation("Deleting cover image for project '{Folder}'", folderId);
            var db = await _session.OpenAsync(folderId);
            var entity = await db.Projects.SingleOrDefaultAsync();
            if (entity?.CoverImage == null)
            {
                _logger.LogDebug("DeleteCoverImageAsync: no cover image set for '{Folder}'", folderId);
                return;
            }

            var imagePath = Path.Combine(_fs.GetProjectFolderPath(folderId), entity.CoverImage);
            if (_fs.FileExists(imagePath))
            {
                _fs.DeleteFile(imagePath);
                _logger.LogDebug("Deleted cover image file: {File}", imagePath);
            }

            entity.CoverImage = null;
            await db.SaveChangesAsync();
            _logger.LogInformation("Cover image removed for project '{Folder}'", folderId);
        }

        public async Task SetNarratorOnlyModeAsync(ProjectFolderId folderId, bool value)
        {
            var db = await _session.OpenAsync(folderId);
            var entity = await db.Projects.SingleOrDefaultAsync();
            if (entity == null)
                throw new ProjectNotFoundException(folderId.Value);
            entity.NarratorOnlyMode = value;
            await db.SaveChangesAsync();
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
    }
}
