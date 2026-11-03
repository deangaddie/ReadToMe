using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Read2Me.Core.IO;
using Read2Me.Core.Models;
using Read2Me.Data.Series;
using Read2Me.Services.Events;
using Read2Me.Services.Mutations;

namespace Read2Me.Services.Series
{
    /// <summary>
    /// The series registries of the Workspace and their lifecycle (#206, ADR 0018): one
    /// <c>.series/{seriesId}/series.db</c> per series, holding its name and its members by project
    /// Id with their Series positions. Books are addressed by folder here and stored by Id.
    /// <para>
    /// Nothing here is a Book mutation: a registry write changes no project, makes no History step
    /// and carries no origin. Every write signals <see cref="SeriesChanged"/> so other clients read
    /// again. A member whose project has gone (deleted outside the app) is dropped the next time its
    /// series is read, as the narrator link self-heals.
    /// </para>
    /// <para>
    /// Every call, reads included, runs under one process-wide lock: the rules span series (a name
    /// is unique in the Workspace, a book is in at most one series) and a read may heal.
    /// </para>
    /// </summary>
    public sealed partial class SeriesRegistry(
        IFileSystem fs,
        IProjectCatalogReader projects,
        ICharacterReader characters,
        BookMutations mutations,
        SeriesRegistryLock gate,
        EventBroadcaster<SeriesChanged> changes,
        ILogger<SeriesRegistry> logger)
    {
        public const string FolderName = ".series";

        /// <summary>The uploaded poster's file name in the series folder, before its extension.</summary>
        public const string PosterName = "poster";

        private const string UploadSuffix = ".upload";

        // ---- reads -------------------------------------------------------------------------------

        /// <summary>Every readable series, by name; each one's books in position order.</summary>
        public async Task<IReadOnlyList<SeriesView>> ListAsync(CancellationToken ct = default)
        {
            using var _ = await gate.EnterAsync(ct);
            var index = await IndexProjectsAsync();
            var (all, _) = await LoadAllAsync(ct);
            var views = new List<SeriesView>(all.Count);
            foreach (var series in all)
                views.Add(await ViewAsync(series, index, ct));
            return views.OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        public async Task<SeriesResult<SeriesView>> GetAsync(Guid seriesId, CancellationToken ct = default)
        {
            using var _ = await gate.EnterAsync(ct);
            var (series, refusal) = await FindAsync(seriesId, ct);
            if (series is null) return refusal!;
            return await ViewAsync(series, await IndexProjectsAsync(), ct);
        }

        /// <summary>Each member folder's series, for the project reads.</summary>
        public async Task<IReadOnlyDictionary<string, ProjectSeries>> MembershipsAsync(CancellationToken ct = default)
        {
            var memberships = new Dictionary<string, ProjectSeries>(StringComparer.OrdinalIgnoreCase);
            foreach (var series in await ListAsync(ct))
                foreach (var book in series.Books)
                    memberships[book.Folder] = new ProjectSeries(series.Id, series.Name, book.Position);
            return memberships;
        }

        /// <summary>
        /// One project's series, or null. Reads only that project and the registries, not every
        /// project, so it neither needs nor does any healing.
        /// </summary>
        public async Task<ProjectSeries?> MembershipOfAsync(ProjectFolderId folder, CancellationToken ct = default)
        {
            using var _ = await gate.EnterAsync(ct);
            if (await projects.GetProjectAsync(folder) is not { } project) return null;
            return (await LoadAllAsync(ct)).All.FirstOrDefault(s => s.Members.ContainsKey(project.Id)) is { } series
                ? new ProjectSeries(series.Id, series.Name, SeriesPositions.Normalize(series.Members[project.Id]))
                : null;
        }

        // ---- series writes -----------------------------------------------------------------------

        /// <summary>Creates a series; with <paramref name="folder"/>, that book joins it as Book 1.</summary>
        public async Task<SeriesResult<Guid>> CreateAsync(string? name, string? folder, CancellationToken ct = default)
        {
            if (Clean(name) is not { } clean) return SeriesRefusal.BlankName;

            using var _ = await gate.EnterAsync(ct);
            var index = await IndexProjectsAsync();
            Guid? projectId = null;
            if (folder is not null)
            {
                if (index.ByFolder(folder) is not { } book) return SeriesRefusal.UnknownFolder(folder);
                projectId = book.ProjectId;
            }

            var (all, unreadable) = await LoadAllAsync(ct);
            if (unreadable > 0) return SeriesRefusal.RulesUnchecked;
            if (NameTaken(all, clean, except: null) is { } taken) return taken;
            if (projectId is { } joining && all.FirstOrDefault(s => s.Members.ContainsKey(joining)) is { } current)
                return SeriesRefusal.InAnotherSeries(index.Title(joining), current.Name);

            var id = Guid.NewGuid();
            await using (var db = await SeriesDbContext.OpenAsync(FolderOf(id), ct))
            {
                db.Series.Add(new SeriesRow { Id = id, Name = clean });
                if (projectId is { } member)
                    db.Books.Add(new SeriesBook { ProjectId = member, Position = SeriesPositions.Next([]) });
                await db.SaveChangesAsync(ct);
            }
            Signal(id, SeriesChangeKind.Changed);
            return id;
        }

        public async Task<SeriesResult<SeriesView>> RenameAsync(Guid seriesId, string? name, CancellationToken ct = default)
        {
            if (Clean(name) is not { } clean) return SeriesRefusal.BlankName;

            using var _ = await gate.EnterAsync(ct);
            var (series, refusal) = await FindAsync(seriesId, ct);
            if (series is null) return refusal!;
            var (all, unreadable) = await LoadAllAsync(ct);
            if (unreadable > 0) return SeriesRefusal.RulesUnchecked;
            if (NameTaken(all, clean, except: seriesId) is { } taken) return taken;

            await using (var db = await SeriesDbContext.OpenAsync(FolderOf(seriesId), ct))
            {
                var row = await db.Series.SingleAsync(ct);
                row.Name = clean;
                await db.SaveChangesAsync(ct);
            }
            Signal(seriesId, SeriesChangeKind.Changed);
            return await ViewAsync(series with { Name = clean }, await IndexProjectsAsync(), ct);
        }

        /// <summary>Every book leaves at once and the registry folder goes; every book keeps its Voices.</summary>
        public async Task<SeriesResult<bool>> DeleteAsync(Guid seriesId, CancellationToken ct = default)
        {
            using var _ = await gate.EnterAsync(ct);
            if (!Directory.Exists(FolderOf(seriesId))) return SeriesRefusal.UnknownSeries;

            SqliteConnection.ClearAllPools();
            Directory.Delete(FolderOf(seriesId), recursive: true);
            Signal(seriesId, SeriesChangeKind.Deleted);
            return true;
        }

        // ---- poster ------------------------------------------------------------------------------

        /// <summary>
        /// Saves the uploaded poster (its type and size already checked against the cover's rules),
        /// replacing any earlier one. No confirm and no History: picking the file is the confirm.
        /// </summary>
        public async Task<SeriesResult<SeriesView>> SetPosterAsync(Guid seriesId, string extension, Stream content, CancellationToken ct = default)
        {
            using var _ = await gate.EnterAsync(ct);
            var (series, refusal) = await FindAsync(seriesId, ct);
            if (series is null) return refusal!;

            // Written aside first, so a failed upload leaves the earlier poster as it was.
            var folder = FolderOf(seriesId);
            var incoming = Path.Combine(folder, PosterName + UploadSuffix);
            try
            {
                await using var file = File.Create(incoming);
                await content.CopyToAsync(file, ct);
            }
            catch
            {
                File.Delete(incoming);
                throw;
            }
            foreach (var earlier in PosterFiles(folder))
                File.Delete(earlier);
            File.Move(incoming, Path.Combine(folder, PosterName + extension.ToLowerInvariant()));

            Signal(seriesId, SeriesChangeKind.Changed);
            return await ViewAsync(series, await IndexProjectsAsync(), ct);
        }

        /// <summary>Removes the uploaded poster; a series without one is fine too. The fallback shows again.</summary>
        public async Task<SeriesResult<bool>> ClearPosterAsync(Guid seriesId, CancellationToken ct = default)
        {
            using var _ = await gate.EnterAsync(ct);
            var (series, refusal) = await FindAsync(seriesId, ct);
            if (series is null) return refusal!;

            var files = PosterFiles(FolderOf(seriesId));
            foreach (var file in files)
                File.Delete(file);
            if (files.Count > 0) Signal(seriesId, SeriesChangeKind.Changed);
            return true;
        }

        // ---- membership --------------------------------------------------------------------------

        /// <summary>
        /// The book joins at the next whole number after the series' highest. Already a member: its
        /// position, unchanged. In another series: refused unless <paramref name="move"/>, which
        /// leaves that one first.
        /// </summary>
        public async Task<SeriesResult<decimal>> JoinAsync(Guid seriesId, string folder, bool move, CancellationToken ct = default)
        {
            using var _ = await gate.EnterAsync(ct);
            var (series, refusal) = await FindAsync(seriesId, ct);
            if (series is null) return refusal!;
            var index = await IndexProjectsAsync();
            if (index.ByFolder(folder) is not { } book) return SeriesRefusal.UnknownFolder(folder);

            if (series.Members.TryGetValue(book.ProjectId, out var held))
                return SeriesPositions.Normalize(held);

            var (all, unreadable) = await LoadAllAsync(ct);
            if (unreadable > 0) return SeriesRefusal.RulesUnchecked;
            var other = all.FirstOrDefault(s => s.Id != seriesId && s.Members.ContainsKey(book.ProjectId));
            if (other is not null)
            {
                if (!move) return SeriesRefusal.InAnotherSeries(book.Title, other.Name);
                await RemoveMemberAsync(other.Id, book.ProjectId, ct);
                Signal(other.Id, SeriesChangeKind.Changed);
            }

            var position = SeriesPositions.Next(series.Members.Values);
            await using (var db = await SeriesDbContext.OpenAsync(FolderOf(seriesId), ct))
            {
                db.Books.Add(new SeriesBook { ProjectId = book.ProjectId, Position = position });
                await db.SaveChangesAsync(ct);
            }
            Signal(seriesId, SeriesChangeKind.Changed);
            return position;
        }

        /// <summary>The book leaves; nobody is renumbered, and the series stays even when empty.</summary>
        public async Task<SeriesResult<bool>> LeaveAsync(Guid seriesId, string folder, CancellationToken ct = default)
        {
            using var _ = await gate.EnterAsync(ct);
            var (series, refusal) = await FindAsync(seriesId, ct);
            if (series is null) return refusal!;
            var index = await IndexProjectsAsync();
            if (index.ByFolder(folder) is not { } book) return SeriesRefusal.UnknownFolder(folder);
            if (!series.Members.ContainsKey(book.ProjectId)) return SeriesRefusal.NotAMember(book.Title, series.Name);

            await RemoveMemberAsync(seriesId, book.ProjectId, ct);
            Signal(seriesId, SeriesChangeKind.Changed);
            return true;
        }

        /// <summary>Saves a series' positions as one set, keyed by folder (<see cref="SeriesPositions.CheckSet"/>).</summary>
        public async Task<SeriesResult<SeriesView>> SetPositionsAsync(
            Guid seriesId, IReadOnlyDictionary<string, decimal> positions, CancellationToken ct = default)
        {
            using var _ = await gate.EnterAsync(ct);
            var (series, refusal) = await FindAsync(seriesId, ct);
            if (series is null) return refusal!;
            var index = await IndexProjectsAsync();
            // A member whose project.db is there but unreadable can't be named in the set, nor left out of it.
            if (!index.Complete && series.Members.Keys.Any(id => index.ById(id) is null))
                return SeriesRefusal.MemberUnreadable;
            var current = await ViewAsync(series, index, ct);

            switch (SeriesPositions.CheckSet(current.Books.Select(b => b.Folder).ToList(), positions))
            {
                case SeriesPositionCheck.Malformed m:
                    return SeriesRefusal.MalformedPosition(m.Book, m.Position);
                case SeriesPositionCheck.OutOfDate:
                    return SeriesRefusal.OutOfDate;
                case SeriesPositionCheck.Tie t:
                    return SeriesRefusal.Tie(index.TitleOf(t.First), index.TitleOf(t.Second), t.Position);
            }

            var byProject = positions.ToDictionary(p => index.ByFolder(p.Key)!.ProjectId, p => p.Value);
            await using (var db = await SeriesDbContext.OpenAsync(FolderOf(seriesId), ct))
            {
                foreach (var row in await db.Books.ToListAsync(ct))
                    row.Position = byProject[row.ProjectId];
                await db.SaveChangesAsync(ct);
            }
            Signal(seriesId, SeriesChangeKind.Changed);
            return await ViewAsync(series with { Members = byProject }, index, ct);
        }

        /// <summary>
        /// A project was deleted: its registry entry goes at once, by the Id read before its folder
        /// went. Not in a series: nothing to do. A registry that can't be read heals on a later read.
        /// </summary>
        public async Task ForgetProjectAsync(Guid projectId, CancellationToken ct = default)
        {
            using var _ = await gate.EnterAsync(ct);
            foreach (var series in (await LoadAllAsync(ct)).All)
            {
                if (!series.Members.ContainsKey(projectId)) continue;
                await RemoveMemberAsync(series.Id, projectId, ct);
                Signal(series.Id, SeriesChangeKind.Changed);
            }
        }

        // ---- internals ---------------------------------------------------------------------------

        private sealed record Loaded(Guid Id, string Name, IReadOnlyDictionary<Guid, decimal> Members);

        private sealed record IndexedProject(string Folder, Guid ProjectId, string Title, string? CoverImage);

        /// <summary>
        /// Every project folder with its Id and title. <see cref="Complete"/> is false when a folder's
        /// project.db is there but could not be read: a member missing from the index may then only be unreadable,
        /// not gone, so nothing is healed away.
        /// </summary>
        private sealed class ProjectIndex(IReadOnlyList<IndexedProject> entries, bool complete)
        {
            private readonly Dictionary<Guid, IndexedProject> _byId = entries
                .GroupBy(e => e.ProjectId).ToDictionary(g => g.Key, g => g.First());

            public bool Complete => complete;
            public IndexedProject? ById(Guid id) => _byId.GetValueOrDefault(id);
            public IndexedProject? ByFolder(string folder) =>
                entries.FirstOrDefault(e => string.Equals(e.Folder, folder, StringComparison.OrdinalIgnoreCase));
            public string Title(Guid id) => ById(id)?.Title ?? id.ToString();
            public string TitleOf(string folder) => ByFolder(folder)?.Title ?? folder;
        }

        private async Task<ProjectIndex> IndexProjectsAsync()
        {
            var entries = new List<IndexedProject>();
            var complete = true;
            foreach (var folder in fs.ListProjectFolders())
            {
                if (!ProjectFolderId.TryParse(folder, out var id)) continue;
                // A folder without a project.db is not a project, so it can't be a member that only looks gone.
                if (!fs.FileExists(Path.Combine(fs.GetProjectFolderPath(id), ProjectDbContextProvider.DbFileName))) continue;
                try
                {
                    if (await projects.GetProjectAsync(id) is { } project)
                        entries.Add(new IndexedProject(folder, project.Id, project.Title, project.CoverImage));
                    else
                        complete = false;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Series: project '{Folder}' could not be read", folder);
                    complete = false;
                }
            }
            return new ProjectIndex(entries, complete);
        }

        private string Root => Path.Combine(fs.WorkspacePath, FolderName);
        private string FolderOf(Guid seriesId) => Path.Combine(Root, seriesId.ToString("D"));

        /// <summary>
        /// Every series whose registry can be read, and how many could not (each logged and left out).
        /// A read shows the rest; a write whose rules span series refuses while any is unreadable.
        /// </summary>
        private async Task<(IReadOnlyList<Loaded> All, int Unreadable)> LoadAllAsync(CancellationToken ct)
        {
            if (!Directory.Exists(Root)) return ([], 0);
            var all = new List<Loaded>();
            var unreadable = 0;
            foreach (var dir in Directory.GetDirectories(Root))
            {
                if (!Guid.TryParse(Path.GetFileName(dir), out var id)) continue;
                try
                {
                    if (await LoadAsync(id, ct) is { } series) all.Add(series);
                }
                catch (Exception ex) when (ex is SqliteException or InvalidOperationException)
                {
                    logger.LogWarning(ex, "Series registry {SeriesId} could not be read; it is left out", id);
                    unreadable++;
                }
            }
            return (all, unreadable);
        }

        private async Task<Loaded?> LoadAsync(Guid seriesId, CancellationToken ct)
        {
            var folder = FolderOf(seriesId);
            if (!File.Exists(Path.Combine(folder, SeriesDbContext.DbFileName))) return null;
            await using var db = await SeriesDbContext.OpenAsync(folder, ct);
            var row = await db.Series.AsNoTracking().SingleOrDefaultAsync(ct);
            if (row is null) return null;
            var members = await db.Books.AsNoTracking().ToDictionaryAsync(b => b.ProjectId, b => b.Position, ct);
            return new Loaded(seriesId, row.Name, members);
        }

        private async Task<(Loaded? Series, SeriesRefusal? Refusal)> FindAsync(Guid seriesId, CancellationToken ct)
        {
            if (!Directory.Exists(FolderOf(seriesId))) return (null, SeriesRefusal.UnknownSeries);
            try
            {
                return await LoadAsync(seriesId, ct) is { } series
                    ? (series, null)
                    : (null, SeriesRefusal.UnknownSeries);
            }
            catch (Exception ex) when (ex is SqliteException or InvalidOperationException)
            {
                logger.LogWarning(ex, "Series registry {SeriesId} could not be read", seriesId);
                return (null, SeriesRefusal.Unreadable);
            }
        }

        /// <summary>The series as the wire shows it, dropping (for good) members whose project is gone.</summary>
        private async Task<SeriesView> ViewAsync(Loaded series, ProjectIndex index, CancellationToken ct)
        {
            var books = new List<SeriesBookView>();
            foreach (var (projectId, position) in series.Members)
            {
                if (index.ById(projectId) is { } project)
                    books.Add(new SeriesBookView(project.Folder, project.Title, SeriesPositions.Normalize(position)));
                else if (index.Complete)
                {
                    // Healing is not a change anyone else needs to hear about: whoever reads next
                    // gets the same answer this read does.
                    logger.LogInformation("Series {SeriesId}: project {ProjectId} is gone; its entry is removed", series.Id, projectId);
                    await RemoveMemberAsync(series.Id, projectId, ct);
                }
            }
            var ordered = books.OrderBy(b => b.Position).ToList();
            return new SeriesView(series.Id, series.Name, ordered, PosterOf(series.Id, ordered, index));
        }

        /// <summary>
        /// The uploaded poster; else the cover of the lowest-positioned book; else none (the client
        /// draws the placeholder). Only that one book's cover counts, not the first book with one.
        /// </summary>
        private SeriesPoster PosterOf(Guid seriesId, IReadOnlyList<SeriesBookView> books, ProjectIndex index)
        {
            if (PosterFiles(FolderOf(seriesId)).FirstOrDefault() is { } uploaded)
                return SeriesPoster.Of(SeriesPosterSource.Uploaded, uploaded, coverPosition: null);

            if (books.FirstOrDefault() is { } first
                && index.ByFolder(first.Folder)?.CoverImage is { Length: > 0 } cover
                && ProjectFolderId.TryParse(first.Folder, out var folderId))
            {
                var path = Path.Combine(fs.GetProjectFolderPath(folderId), Path.GetFileName(cover));
                if (File.Exists(path))
                    return SeriesPoster.Of(SeriesPosterSource.Cover, path, first.Position);
            }
            return SeriesPoster.None;
        }

        private static IReadOnlyList<string> PosterFiles(string folder) =>
            Directory.Exists(folder)
                ? Directory.GetFiles(folder, PosterName + ".*")
                    .Where(f => !f.EndsWith(UploadSuffix, StringComparison.OrdinalIgnoreCase))
                    .Order(StringComparer.OrdinalIgnoreCase)
                    .ToList()
                : [];

        /// <summary>
        /// The book leaves: its links go with it, and so does a Series character left with no linked
        /// book. The book keeps its own copy of every Voice.
        /// </summary>
        private async Task RemoveMemberAsync(Guid seriesId, Guid projectId, CancellationToken ct)
        {
            await using var db = await SeriesDbContext.OpenAsync(FolderOf(seriesId), ct);
            await db.Books.Where(b => b.ProjectId == projectId).ExecuteDeleteAsync(ct);
            await RemoveLinksAsync(db, l => l.ProjectId == projectId, ct);
        }

        private static string? Clean(string? name) => string.IsNullOrWhiteSpace(name) ? null : name.Trim();

        private static SeriesRefusal? NameTaken(IEnumerable<Loaded> all, string name, Guid? except) =>
            all.FirstOrDefault(s => s.Id != except && string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)) is { } holder
                ? SeriesRefusal.NameTaken(holder.Name)
                : null;

        private void Signal(Guid seriesId, SeriesChangeKind kind) => changes.Publish(new SeriesChanged(seriesId, kind));
    }

    /// <summary>One series as the API reads it: its books in position order.</summary>
    public sealed record SeriesView(Guid Id, string Name, IReadOnlyList<SeriesBookView> Books, SeriesPoster Poster);

    public enum SeriesPosterSource
    {
        Uploaded,
        Cover,
        None,
    }

    /// <summary>
    /// What a series card shows (#206 item 11): the file served for it, if any, with a
    /// <see cref="Version"/> that changes whenever that file does, and for a fallback cover the
    /// position of the book it comes from.
    /// </summary>
    public sealed record SeriesPoster(SeriesPosterSource Source, string? Path, long Version, decimal? CoverPosition)
    {
        public static readonly SeriesPoster None = new(SeriesPosterSource.None, null, 0, null);

        public static SeriesPoster Of(SeriesPosterSource source, string path, decimal? coverPosition) =>
            new(source, path, File.GetLastWriteTimeUtc(path).Ticks, coverPosition);
    }

    public sealed record SeriesBookView(string Folder, string Title, decimal Position);

    /// <summary>The series a project is in, as its project reads show it.</summary>
    public sealed record ProjectSeries(Guid SeriesId, string Name, decimal Position);

    /// <summary>
    /// The one lock every registry call takes (process-wide, so a singleton): a name's uniqueness and
    /// a book's single series are facts across registries.
    /// </summary>
    public sealed class SeriesRegistryLock
    {
        private readonly SemaphoreSlim _semaphore = new(1, 1);

        public async Task<IDisposable> EnterAsync(CancellationToken ct)
        {
            await _semaphore.WaitAsync(ct);
            return new Releaser(_semaphore);
        }

        private sealed class Releaser(SemaphoreSlim semaphore) : IDisposable
        {
            private int _released;
            public void Dispose()
            {
                if (Interlocked.Exchange(ref _released, 1) == 0) semaphore.Release();
            }
        }
    }
}
