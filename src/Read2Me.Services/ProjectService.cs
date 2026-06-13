using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Read2Me.Core.IO;
using Read2Me.Core.Models;
using Read2Me.Data;
using Read2Me.Data.Enums;
using ProjectEntity = Read2Me.Data.Entities.Project;

namespace Read2Me.Services
{
    public class ProjectService : IProjectReader, IProjectWriter
    {
        private readonly IFileSystem _fs;
        private readonly IProjectDbContextFactory _dbFactory;
        private readonly ILogger<ProjectService> _logger;

        public ProjectService(IFileSystem fs, IProjectDbContextFactory dbFactory, ILogger<ProjectService> logger)
        {
            _fs = fs;
            _dbFactory = dbFactory;
            _logger = logger;
        }

        public IReadOnlyList<string> GetProjects()
        {
            var folders = _fs.ListProjectFolders();
            _logger.LogDebug("Found {Count} project(s) in workspace", folders.Count);
            return folders;
        }

        public string SanitizeName(string name)
        {
            var s = name.ToLowerInvariant().Replace(' ', '-');
            s = Regex.Replace(s, @"[^\w\-]", "");
            s = Regex.Replace(s, @"-{2,}", "-").Trim('-');
            return s;
        }

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
            var folderName = SanitizeName(title);
            if (string.IsNullOrEmpty(folderName))
                throw new ArgumentException("Title produces an empty folder name.", nameof(title));

            if (_fs.ProjectFolderExists(folderName))
                throw new InvalidOperationException($"A project named \"{folderName}\" already exists.");

            _logger.LogInformation("Creating project '{Title}' in folder '{Folder}'", title, folderName);

            _fs.CreateProjectFolder(folderName);

            var destFile = Path.Combine(_fs.GetProjectFolderPath(folderName), originalFileName);
            await _fs.WriteFileAsync(destFile, fileStream);
            _logger.LogDebug("Saved book file: {File}", destFile);

            await using var db = await OpenProjectDbAsync(folderName);

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
            return folderName;
        }

        public async Task<IReadOnlyList<ProjectSummary>> GetProjectSummariesAsync()
        {
            var folders = GetProjects();
            var summaries = new List<ProjectSummary>(folders.Count);
            foreach (var folder in folders)
            {
                var project = await GetProjectAsync(folder);
                summaries.Add(new ProjectSummary(folder, project?.Title ?? folder));
            }
            return summaries;
        }

        public async Task<ProjectEntity?> GetProjectAsync(string folderName)
        {
            var dbPath = Path.Combine(_fs.GetProjectFolderPath(folderName), "project.db");
            if (!_fs.FileExists(dbPath))
            {
                _logger.LogWarning("GetProjectAsync: no DB found for folder '{Folder}'", folderName);
                return null;
            }

            await using var db = await OpenProjectDbAsync(folderName);
            return await db.Projects.FirstOrDefaultAsync();
        }

        public async Task SaveCoverImageAsync(string folderName, string filename, Stream stream)
        {
            _logger.LogInformation("Saving cover image '{File}' for project '{Folder}'", filename, folderName);
            await using var db = await OpenProjectDbAsync(folderName);
            var entity = await db.Projects.FirstOrDefaultAsync();
            if (entity == null)
            {
                _logger.LogWarning("SaveCoverImageAsync: project record not found for '{Folder}'", folderName);
                return;
            }

            var folderPath = _fs.GetProjectFolderPath(folderName);
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
            _logger.LogInformation("Cover image saved for project '{Folder}'", folderName);
        }

        public async Task DeleteCoverImageAsync(string folderName)
        {
            _logger.LogInformation("Deleting cover image for project '{Folder}'", folderName);
            await using var db = await OpenProjectDbAsync(folderName);
            var entity = await db.Projects.FirstOrDefaultAsync();
            if (entity?.CoverImage == null)
            {
                _logger.LogDebug("DeleteCoverImageAsync: no cover image set for '{Folder}'", folderName);
                return;
            }

            var imagePath = Path.Combine(_fs.GetProjectFolderPath(folderName), entity.CoverImage);
            if (_fs.FileExists(imagePath))
            {
                _fs.DeleteFile(imagePath);
                _logger.LogDebug("Deleted cover image file: {File}", imagePath);
            }

            entity.CoverImage = null;
            await db.SaveChangesAsync();
            _logger.LogInformation("Cover image removed for project '{Folder}'", folderName);
        }

        public async Task<bool> HasBookContentAsync(string folderName)
        {
            var dbPath = Path.Combine(_fs.GetProjectFolderPath(folderName), "project.db");
            if (!_fs.FileExists(dbPath))
                return false;

            await using var db = await OpenProjectDbAsync(folderName);
            return await db.Volumes.AnyAsync();
        }

        public async Task<List<Read2Me.Data.Entities.Volume>> GetVolumesAsync(string folderName)
        {
            await using var db = await OpenProjectDbAsync(folderName);
            return await db.Volumes.OrderBy(v => v.Order).ToListAsync();
        }

        public async Task<List<Read2Me.Data.Entities.Part>> GetPartsAsync(string folderName, Guid volumeId)
        {
            await using var db = await OpenProjectDbAsync(folderName);
            return await db.Parts.Where(p => p.VolumeId == volumeId).OrderBy(p => p.Order).ToListAsync();
        }

        public async Task<List<Read2Me.Data.Entities.Chapter>> GetChaptersAsync(string folderName, Guid partId)
        {
            await using var db = await OpenProjectDbAsync(folderName);
            return await db.Chapters.Where(c => c.PartId == partId).OrderBy(c => c.Order).ToListAsync();
        }

        public async Task<List<Read2Me.Data.Entities.Paragraph>> GetChapterParagraphsAsync(string folderName, Guid chapterId)
        {
            await using var db = await OpenProjectDbAsync(folderName);
            return await db.Paragraphs
                .Where(p => p.ChapterId == chapterId)
                .OrderBy(p => p.Order)
                .Include(p => p.Items.OrderBy(i => i.Order))
                .ToListAsync();
        }

        public async Task ClearBookContentAsync(string folderName)
        {
            await using var db = await OpenProjectDbAsync(folderName);
            await using var tx = await db.Database.BeginTransactionAsync();
            await db.ParagraphItems.ExecuteDeleteAsync();
            await db.Paragraphs.ExecuteDeleteAsync();
            await db.Chapters.ExecuteDeleteAsync();
            await db.Parts.ExecuteDeleteAsync();
            await db.Volumes.ExecuteDeleteAsync();
            await tx.CommitAsync();
        }

        public void DeleteProject(string folderName)
        {
            if (_fs.ProjectFolderExists(folderName))
            {
                _logger.LogInformation("Deleting project '{Folder}'", folderName);
                _fs.DeleteProjectFolder(folderName);
                _logger.LogInformation("Project '{Folder}' deleted", folderName);
            }
            else
            {
                _logger.LogWarning("DeleteProject: folder not found '{Folder}'", folderName);
            }
        }

        private Task<ProjectDbContext> OpenProjectDbAsync(string folderName)
        {
            var folderPath = _fs.GetProjectFolderPath(folderName);
            _logger.LogDebug("Opening project DB: {FolderPath}", folderPath);
            return _dbFactory.CreateAsync(folderPath);
        }
    }
}
