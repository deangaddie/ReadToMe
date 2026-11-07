using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
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
    /// again. Reads are answered from a <see cref="SeriesSnapshot"/>, which filters stale rows (a
    /// member whose project has gone, a link to a Character that has gone) and deletes nothing; a
    /// write that loads a series deletes that series' stale rows first (writes heal).
    /// </para>
    /// <para>
    /// Every write runs under one process-wide lock and loads its snapshot inside it: the rules span
    /// series (a name is unique in the Workspace, a book is in at most one series). Reads take no
    /// lock, so they don't wait behind a series Voice copy; one made while a copy runs may briefly
    /// show a book as differing, and the <c>series</c> signal that ends the copy makes the client
    /// read again.
    /// </para>
    /// </summary>
    public sealed partial class SeriesRegistry(
        ICharacterReader characters,
        SeriesSnapshotLoader loader,
        SeriesRegistryLock gate,
        EventBroadcaster<SeriesChanged> changes,
        SeriesVoiceReplicator replicator,
        ILogger<SeriesRegistry> logger)
    {
        public const string FolderName = ".series";

        /// <summary>The uploaded poster's file name in the series folder, before its extension.</summary>
        public const string PosterName = "poster";

        // ---- reads -------------------------------------------------------------------------------

        /// <summary>Every readable series, by name; each one's books in position order.</summary>
        public async Task<IReadOnlyList<SeriesView>> ListAsync(CancellationToken ct = default) =>
            (await loader.LoadWorkspaceAsync(ct)).List();

        public async Task<SeriesResult<SeriesView>> GetAsync(Guid seriesId, CancellationToken ct = default)
        {
            var loaded = await loader.LoadSeriesAsync(seriesId, withCasts: false, ct);
            return loaded.Value is { } series ? series.View() : loaded.Refusal!;
        }

        /// <summary>Each member folder's series, for the project reads.</summary>
        public async Task<IReadOnlyDictionary<string, ProjectSeries>> MembershipsAsync(CancellationToken ct = default) =>
            (await loader.LoadWorkspaceAsync(ct)).Memberships();

        /// <summary>One project's series, or null. Reads only that project and the registries, not every project.</summary>
        public async Task<ProjectSeries?> MembershipOfAsync(ProjectFolderId folder, CancellationToken ct = default)
        {
            return await loader.LoadForProjectAsync(folder, ct) is { } found
                ? found.Workspace.MembershipOf(found.ProjectId)
                : null;
        }

        // ---- series writes -----------------------------------------------------------------------

        /// <summary>Creates a series; with <paramref name="folder"/>, that book joins it as Book 1.</summary>
        public async Task<SeriesResult<Guid>> CreateAsync(string? name, string? folder, CancellationToken ct = default)
        {
            if (Clean(name) is not { } clean) return SeriesRefusal.BlankName;

            using var _ = await gate.EnterAsync(ct);
            var index = await loader.IndexProjectsAsync();
            Guid? projectId = null;
            if (folder is not null)
            {
                if (index.ByFolder(folder) is not { } book) return SeriesRefusal.UnknownFolder(folder);
                projectId = book.ProjectId;
            }

            var (all, unreadable) = await loader.LoadRecordsAsync(ct);
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
            var loaded = await LoadHealedAsync(seriesId, ct, withCasts: false);
            if (loaded.Value is null) return loaded.Refusal!;
            var (all, unreadable) = await loader.LoadRecordsAsync(ct);
            if (unreadable > 0) return SeriesRefusal.RulesUnchecked;
            if (NameTaken(all, clean, except: seriesId) is { } taken) return taken;

            await using (var db = await SeriesDbContext.OpenAsync(FolderOf(seriesId), ct))
            {
                var row = await db.Series.SingleAsync(ct);
                row.Name = clean;
                await db.SaveChangesAsync(ct);
            }
            Signal(seriesId, SeriesChangeKind.Changed);
            return loaded.Value.View() with { Name = clean };
        }

        /// <summary>Every book leaves at once and the registry folder goes; every book keeps its Voices.</summary>
        public async Task<SeriesResult<bool>> DeleteAsync(Guid seriesId, CancellationToken ct = default)
        {
            using var _ = await gate.EnterAsync(ct);
            if (!Directory.Exists(FolderOf(seriesId))) return SeriesRefusal.UnknownSeries;

            SqliteConnection.ClearAllPools();
            await DeleteFolderAsync(FolderOf(seriesId), ct);
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
            var loaded = await LoadHealedAsync(seriesId, ct, withCasts: false);
            if (loaded.Value is null) return loaded.Refusal!;

            // Written aside first, so a failed upload leaves the earlier poster as it was.
            var folder = FolderOf(seriesId);
            var incoming = SeriesSnapshotLoader.UploadPath(folder);
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
            foreach (var earlier in SeriesSnapshotLoader.PosterFiles(folder))
                File.Delete(earlier);
            File.Move(incoming, Path.Combine(folder, PosterName + extension.ToLowerInvariant()));

            Signal(seriesId, SeriesChangeKind.Changed);
            var after = await loader.LoadSeriesAsync(seriesId, withCasts: false, ct);
            return after.Value is { } series ? series.View() : after.Refusal!;
        }

        /// <summary>Removes the uploaded poster; a series without one is fine too. The fallback shows again.</summary>
        public async Task<SeriesResult<bool>> ClearPosterAsync(Guid seriesId, CancellationToken ct = default)
        {
            using var _ = await gate.EnterAsync(ct);
            var loaded = await LoadHealedAsync(seriesId, ct, withCasts: false);
            if (loaded.Value is null) return loaded.Refusal!;

            var files = SeriesSnapshotLoader.PosterFiles(FolderOf(seriesId));
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
            var loaded = await LoadHealedAsync(seriesId, ct, withCasts: false);
            if (loaded.Value is not { } series) return loaded.Refusal!;
            if (series.Index.ByFolder(folder) is not { } book) return SeriesRefusal.UnknownFolder(folder);

            if (series.Members.TryGetValue(book.ProjectId, out var held))
                return SeriesPositions.Normalize(held);

            var (all, unreadable) = await loader.LoadRecordsAsync(ct);
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
            var loaded = await LoadHealedAsync(seriesId, ct, withCasts: false);
            if (loaded.Value is not { } series) return loaded.Refusal!;
            if (series.Index.ByFolder(folder) is not { } book) return SeriesRefusal.UnknownFolder(folder);
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
            var loaded = await LoadHealedAsync(seriesId, ct, withCasts: false);
            if (loaded.Value is not { } series) return loaded.Refusal!;
            var index = series.Index;
            // A member whose project.db is there but unreadable can't be named in the set, nor left out of it.
            if (!index.Complete && series.Members.Keys.Any(id => index.ById(id) is null))
                return SeriesRefusal.MemberUnreadable;

            switch (SeriesPositions.CheckSet(series.Books.Select(b => b.Folder).ToList(), positions))
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
            var after = await loader.LoadSeriesAsync(seriesId, withCasts: false, ct);
            return after.Value is { } saved ? saved.View() : after.Refusal!;
        }

        /// <summary>
        /// A project was deleted: its registry entry goes at once, by the Id read before its folder
        /// went. Not in a series: nothing to do. A registry that can't be read heals on a later write.
        /// </summary>
        public async Task ForgetProjectAsync(Guid projectId, CancellationToken ct = default)
        {
            using var _ = await gate.EnterAsync(ct);
            foreach (var series in (await loader.LoadRecordsAsync(ct)).All)
            {
                if (!series.Members.ContainsKey(projectId)) continue;
                await RemoveMemberAsync(series.Id, projectId, ct);
                Signal(series.Id, SeriesChangeKind.Changed);
            }
        }

        // ---- internals ---------------------------------------------------------------------------

        private string FolderOf(Guid seriesId) => loader.FolderOf(seriesId);

        /// <summary>
        /// A write's series, with its member books' Characters, after deleting the stale rows its
        /// snapshot filters out: members whose project is gone, links whose book left or whose
        /// Character is gone (and a Series character left with none), and <i>Not the same</i> whose
        /// Character is gone or now linked. Healing is not a change anyone else needs to hear about:
        /// whoever reads next gets the same answer either way.
        /// </summary>
        private async Task<SeriesResult<SeriesSnapshot>> LoadHealedAsync(Guid seriesId, CancellationToken ct, bool withCasts = true)
        {
            var loaded = await loader.LoadSeriesAsync(seriesId, withCasts, ct);
            if (loaded.Value is { } series) await HealAsync(series, ct);
            return loaded;
        }

        /// <summary>Deletes the stale rows <paramref name="series"/> filters out; only writes call it, under the lock.</summary>
        private async Task HealAsync(SeriesSnapshot series, CancellationToken ct)
        {
            if (!series.HasStaleRows) return;
            await using var db = await SeriesDbContext.OpenAsync(FolderOf(series.Id), ct);
            if (series.StaleMembers.Count > 0)
            {
                logger.LogInformation("Series {SeriesId}: {Count} members' projects are gone; their entries are removed", series.Id, series.StaleMembers.Count);
                var gone = series.StaleMembers.ToList();
                await db.Books.Where(b => gone.Contains(b.ProjectId)).ExecuteDeleteAsync(ct);
            }
            if (series.StaleLinks.Count > 0)
            {
                logger.LogInformation("Series {SeriesId}: {Count} links to Characters that are gone are removed", series.Id, series.StaleLinks.Count);
                var stale = series.StaleLinks.ToHashSet();
                await RemoveLinksAsync(db, stale.Contains, ct);
            }
            if (series.StaleNotTheSame.Count > 0)
            {
                var ids = series.StaleNotTheSame.Select(n => n.Id).ToList();
                await db.NotTheSame.Where(n => ids.Contains(n.Id)).ExecuteDeleteAsync(ct);
            }
        }

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

        /// <summary>
        /// Deletes a series folder. A read takes no lock, so one may still have series.db open for a
        /// moment (Windows then refuses the delete): it is tried again a few times before failing.
        /// </summary>
        private static async Task DeleteFolderAsync(string folder, CancellationToken ct)
        {
            for (var attempt = 1; Directory.Exists(folder); attempt++)
            {
                try
                {
                    Directory.Delete(folder, recursive: true);
                    return;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 5)
                {
                    await Task.Delay(50 * attempt, ct);
                }
            }
        }

        private static string? Clean(string? name) => string.IsNullOrWhiteSpace(name) ? null : name.Trim();

        private static SeriesRefusal? NameTaken(IEnumerable<SeriesRecord> all, string name, Guid? except) =>
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
    /// The one lock every registry write takes (process-wide, so a singleton; reads take none): a name's uniqueness and
    /// a book's single series are facts across registries. A <see cref="Hold"/> is the proof that a
    /// caller holds it, for work that runs inside the caller's hold (<see cref="SeriesVoiceReplicator"/>).
    /// </summary>
    public sealed class SeriesRegistryLock
    {
        private readonly SemaphoreSlim _semaphore = new(1, 1);

        public async Task<Hold> EnterAsync(CancellationToken ct)
        {
            await _semaphore.WaitAsync(ct);
            return new Hold(_semaphore);
        }

        /// <summary>The lock, held until disposed.</summary>
        public sealed class Hold : IDisposable
        {
            private readonly SemaphoreSlim _semaphore;
            private int _released;

            internal Hold(SemaphoreSlim semaphore) => _semaphore = semaphore;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _released, 1) == 0) _semaphore.Release();
            }
        }
    }
}
