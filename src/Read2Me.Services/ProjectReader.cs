using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Read2Me.Core.Models;
using ProjectEntity = Read2Me.Data.Entities.Project;

namespace Read2Me.Services
{
    /// <summary>
    /// Read-side query facade over a project's SQLite DB.
    /// Split by area: catalog (this file), ProjectReader.Book.cs,
    /// ProjectReader.Characters.cs, ProjectReader.Audio.cs.
    /// </summary>
    public partial class ProjectReader : IProjectReader
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
    }
}
