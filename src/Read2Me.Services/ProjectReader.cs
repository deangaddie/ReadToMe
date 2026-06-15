using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Read2Me.Core.Models;
using Read2Me.Data.Entities;
using ProjectEntity = Read2Me.Data.Entities.Project;

namespace Read2Me.Services
{
    public class ProjectReader : IProjectReader
    {
        private readonly ProjectDbSession _session;
        private readonly ILogger<ProjectReader> _logger;

        public ProjectReader(ProjectDbSession session, ILogger<ProjectReader> logger)
        {
            _session = session;
            _logger = logger;
        }

        public IReadOnlyList<string> GetProjects()
        {
            var folders = _session.FileSystem.ListProjectFolders();
            _logger.LogDebug("Found {Count} project(s) in workspace", folders.Count);
            return folders;
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

        public async Task<ProjectEntity?> GetProjectAsync(ProjectFolderId folderId)
        {
            var dbPath = Path.Combine(_session.FileSystem.GetProjectFolderPath(folderId), "project.db");
            if (!_session.FileSystem.FileExists(dbPath))
            {
                _logger.LogWarning("GetProjectAsync: no DB found for folder '{Folder}'", folderId);
                return null;
            }

            var db = await _session.OpenAsync(folderId);
            return await db.Projects.SingleOrDefaultAsync();
        }

        public async Task<bool> HasBookContentAsync(ProjectFolderId folderId)
        {
            var dbPath = Path.Combine(_session.FileSystem.GetProjectFolderPath(folderId), "project.db");
            if (!_session.FileSystem.FileExists(dbPath))
                return false;

            var db = await _session.OpenAsync(folderId);
            return await db.Volumes.AnyAsync();
        }

        public async Task<List<Volume>> GetVolumesAsync(ProjectFolderId folderId)
        {
            var db = await _session.OpenAsync(folderId);
            return await db.Volumes.OrderBy(v => v.Order).ToListAsync();
        }

        public async Task<List<Part>> GetPartsAsync(ProjectFolderId folderId, Guid volumeId)
        {
            var db = await _session.OpenAsync(folderId);
            return await db.Parts.Where(p => p.VolumeId == volumeId).OrderBy(p => p.Order).ToListAsync();
        }

        public async Task<List<Chapter>> GetChaptersAsync(ProjectFolderId folderId, Guid partId)
        {
            var db = await _session.OpenAsync(folderId);
            return await db.Chapters.Where(c => c.PartId == partId).OrderBy(c => c.Order).ToListAsync();
        }

        public async Task<List<Paragraph>> GetChapterParagraphsAsync(ProjectFolderId folderId, Guid chapterId)
        {
            var db = await _session.OpenAsync(folderId);
            return await db.Paragraphs
                .Where(p => p.ChapterId == chapterId)
                .OrderBy(p => p.Order)
                .Include(p => p.Items.OrderBy(i => i.Order))
                    .ThenInclude(i => i.Character)
                .ToListAsync();
        }

        public async Task<List<Character>> GetCharactersAsync(ProjectFolderId folderId)
        {
            var db = await _session.OpenAsync(folderId);
            return await db.Characters.OrderBy(c => c.IsNarrator ? 0 : 1).ThenBy(c => c.Name).ToListAsync();
        }

        public async Task<int> GetTotalPartCountAsync(ProjectFolderId folderId)
        {
            var db = await _session.OpenAsync(folderId);
            return await db.Parts.CountAsync();
        }

        public async Task<int> GetTotalChapterCountAsync(ProjectFolderId folderId)
        {
            var db = await _session.OpenAsync(folderId);
            return await db.Chapters.CountAsync();
        }
    }
}
