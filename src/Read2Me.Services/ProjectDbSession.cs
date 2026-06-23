using Microsoft.Extensions.Logging;
using Read2Me.Core.IO;
using Read2Me.Core.Models;
using Read2Me.Data;

namespace Read2Me.Services
{
    public class ProjectDbSession : IAsyncDisposable
    {
        private readonly IFileSystem _fs;
        private readonly IProjectDbContextFactory _dbFactory;
        private readonly ILogger<ProjectDbSession> _logger;
        private readonly Dictionary<string, ProjectDbContext> _contextCache = new(StringComparer.OrdinalIgnoreCase);

        public ProjectDbSession(IFileSystem fs, IProjectDbContextFactory dbFactory, ILogger<ProjectDbSession> logger)
        {
            _fs = fs;
            _dbFactory = dbFactory;
            _logger = logger;
        }

        public IFileSystem FileSystem => _fs;

        public async Task<ProjectDbContext> OpenAsync(ProjectFolderId folderId)
        {
            var folderPath = _fs.GetProjectFolderPath(folderId);
            if (_contextCache.TryGetValue(folderId, out var cached))
                return cached;
            _logger.LogDebug("Opening project DB: {FolderPath}", folderPath);
            var ctx = await _dbFactory.CreateAsync(folderPath);
            _contextCache[folderId] = ctx;
            return ctx;
        }

        public void Evict(ProjectFolderId folderId)
        {
            if (_contextCache.Remove(folderId, out var ctx))
                ctx.Dispose();
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var ctx in _contextCache.Values)
                await ctx.DisposeAsync();
            _contextCache.Clear();
        }
    }
}
