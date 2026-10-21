using Microsoft.Extensions.Logging;
using Read2Me.Core.IO;
using Read2Me.Core.Models;
using Read2Me.Data;
using Read2Me.Services.History;

namespace Read2Me.Services
{
    /// <summary>
    /// The project DB contexts one scope reads and writes through, one per folder.
    /// <para>
    /// Each folder has a <b>state reference</b> (spec, "Read side: a state reference"): the tip by
    /// default, or a History step set by <see cref="ReadAt"/>, which a request reading at
    /// <c>?at=</c> sets before its handler runs. The session resolves it, so the readers only open
    /// the folder as usual and read the past without knowing. Writes only ever go to the tip: a
    /// step's context is read-only.
    /// </para>
    /// </summary>
    public class ProjectDbSession : IAsyncDisposable
    {
        private readonly IFileSystem _fs;
        private readonly IProjectDbContextFactory _dbFactory;
        private readonly ILogger<ProjectDbSession> _logger;
        private readonly Dictionary<string, ProjectDbContext> _contextCache = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, StepState> _states = new(StringComparer.OrdinalIgnoreCase);

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
            ProjectDbContext ctx;
            if (_states.TryGetValue(folderId, out var state))
            {
                _logger.LogDebug("Opening project DB at History step {Step}: {FolderPath}", state.Step.StepId, folderPath);
                ctx = state.OpenContext();
            }
            else
            {
                _logger.LogDebug("Opening project DB: {FolderPath}", folderPath);
                ctx = await _dbFactory.CreateAsync(folderPath);
            }
            _contextCache[folderId] = ctx;
            return ctx;
        }

        /// <summary>
        /// Points the folder's state reference at an opened History step: every later
        /// <see cref="OpenAsync"/> in this scope reads that step's state instead of the tip.
        /// </summary>
        public void ReadAt(ProjectFolderId folderId, StepState state)
        {
            Evict(folderId);
            _states[folderId] = state;
        }

        /// <summary>The History step the folder is read at, or null when it reads the tip.</summary>
        public StepState? StateOf(ProjectFolderId folderId) => _states.GetValueOrDefault(folderId);

        public void Evict(ProjectFolderId folderId)
        {
            if (_contextCache.Remove(folderId, out var ctx))
                ctx.Dispose();
        }

        /// <summary>
        /// Drops what the cached context has tracked for a project, so the next read builds its
        /// entities from the database instead of returning the ones it materialised before someone
        /// else's write.
        /// <para>
        /// The gentler half of <see cref="Evict"/>, for a reader that did not do the writing: it
        /// leaves the context alive. Evicting would dispose a <see cref="ProjectDbContext"/> another
        /// part of the same scope may be holding, which is not something a reader converging in the
        /// background is entitled to do.
        /// </para>
        /// </summary>
        public void Refresh(ProjectFolderId folderId)
        {
            if (_contextCache.TryGetValue(folderId, out var ctx))
                ctx.ChangeTracker.Clear();
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var ctx in _contextCache.Values)
                await ctx.DisposeAsync();
            _contextCache.Clear();
        }
    }
}
