using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Read2Me.Core.IO;
using Read2Me.Core.Models;
using Read2Me.Data.Series;

namespace Read2Me.Services.Series
{
    /// <summary>
    /// Builds <see cref="SeriesSnapshot"/>s (#234): the registry layer from the <c>.series/{id}/series.db</c>
    /// files and the project catalog, when asked each member book's Characters, and the Voices layer
    /// for the Series characters a call names (<see cref="WithVoicesAsync"/>). It only reads: stale
    /// rows are left in place for a write to heal.
    /// </summary>
    public sealed partial class SeriesSnapshotLoader(
        IFileSystem fs,
        IProjectCatalogReader projects,
        ICharacterReader characters,
        VoiceAudioHashes audioHashes,
        ILogger<SeriesSnapshotLoader> logger)
    {
        private const string UploadSuffix = ".upload";

        private string Root => Path.Combine(fs.WorkspacePath, SeriesRegistry.FolderName);

        public string FolderOf(Guid seriesId) => Path.Combine(Root, seriesId.ToString("D"));

        // ---- snapshots ---------------------------------------------------------------------------

        /// <summary>Every readable series' registry layer, over the whole project index; no Characters.</summary>
        public async Task<SeriesWorkspaceSnapshot> LoadWorkspaceAsync(CancellationToken ct = default)
        {
            var index = await IndexProjectsAsync();
            var (records, unreadable) = await LoadRecordsAsync(ct);
            return Workspace(records, index, unreadable);
        }

        /// <summary>
        /// The registry layer of every series, over an index of this one project only: enough for its
        /// membership without reading every project.
        /// </summary>
        public async Task<(SeriesWorkspaceSnapshot Workspace, Guid ProjectId)?> LoadForProjectAsync(ProjectFolderId folder, CancellationToken ct = default)
        {
            if (await projects.GetProjectAsync(folder) is not { } project) return null;
            var index = new SeriesProjectIndex([new SeriesProject(folder.Value, project.Id, project.Title)], complete: false);
            var (records, unreadable) = await LoadRecordsAsync(ct);
            return (Workspace(records, index, unreadable), project.Id);
        }

        /// <summary>One series, or why not; with <paramref name="withCasts"/>, its member books' Characters too.</summary>
        public async Task<SeriesResult<SeriesSnapshot>> LoadSeriesAsync(Guid seriesId, bool withCasts, CancellationToken ct = default)
        {
            var found = await FindRecordAsync(seriesId, ct);
            if (found.Value is not { } record) return found.Refusal!;
            return await SnapshotAsync(record, await IndexProjectsAsync(), withCasts, ct);
        }

        /// <summary>The series this project is a member of, with the project's Id; null when it is in none.</summary>
        public async Task<(SeriesSnapshot Series, Guid ProjectId)?> LoadSeriesOfAsync(ProjectFolderId folder, bool withCasts, CancellationToken ct = default)
        {
            if (await projects.GetProjectAsync(folder) is not { } project) return null;
            var (records, _) = await LoadRecordsAsync(ct);
            if (records.FirstOrDefault(r => r.Members.ContainsKey(project.Id)) is not { } record) return null;
            return (await SnapshotAsync(record, await IndexProjectsAsync(), withCasts, ct), project.Id);
        }

        private async Task<SeriesSnapshot> SnapshotAsync(SeriesRecord record, SeriesProjectIndex index, bool withCasts, CancellationToken ct)
        {
            var bare = new SeriesSnapshot(record, index);
            return withCasts ? new SeriesSnapshot(record, index, await CastsAsync(bare, ct)) : bare;
        }

        private static SeriesWorkspaceSnapshot Workspace(IReadOnlyList<SeriesRecord> records, SeriesProjectIndex index, int unreadable) =>
            new(records.Select(r => new SeriesSnapshot(r, index)).ToList(), unreadable);

        // ---- the registry layer ------------------------------------------------------------------

        /// <summary>
        /// Every project folder with its Id, title and cover. Not <see cref="SeriesProjectIndex.Complete"/>
        /// when a folder's project.db is there but could not be read.
        /// </summary>
        public async Task<SeriesProjectIndex> IndexProjectsAsync()
        {
            var entries = new List<SeriesProject>();
            var complete = true;
            foreach (var folder in fs.ListProjectFolders())
            {
                if (!ProjectFolderId.TryParse(folder, out var id)) continue;
                // A folder without a project.db is not a project, so it can't be a member that only looks gone.
                var projectFolder = fs.GetProjectFolderPath(id);
                if (!fs.FileExists(Path.Combine(projectFolder, ProjectDbContextProvider.DbFileName))) continue;
                try
                {
                    if (await projects.GetProjectAsync(id) is { } project)
                        entries.Add(new SeriesProject(folder, project.Id, project.Title, CoverOf(projectFolder, project.CoverImage)));
                    else
                        complete = false;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Series: project '{Folder}' could not be read", folder);
                    complete = false;
                }
            }
            return new SeriesProjectIndex(entries, complete);
        }

        /// <summary>
        /// Every series whose registry can be read, and how many could not (each logged and left out).
        /// A read shows the rest; a write whose rules span series refuses while any is unreadable.
        /// </summary>
        public async Task<(IReadOnlyList<SeriesRecord> All, int Unreadable)> LoadRecordsAsync(CancellationToken ct = default)
        {
            if (!Directory.Exists(Root)) return ([], 0);
            var all = new List<SeriesRecord>();
            var unreadable = 0;
            foreach (var dir in Directory.GetDirectories(Root))
            {
                if (!Guid.TryParse(Path.GetFileName(dir), out var id)) continue;
                try
                {
                    if (await LoadRecordAsync(id, ct) is { } series) all.Add(series);
                }
                catch (Exception ex) when (ex is SqliteException or InvalidOperationException)
                {
                    logger.LogWarning(ex, "Series registry {SeriesId} could not be read; it is left out", id);
                    unreadable++;
                }
            }
            return (all, unreadable);
        }

        /// <summary>One series' registry layer, or why not: no such series, or its registry can't be read.</summary>
        public async Task<SeriesResult<SeriesRecord>> FindRecordAsync(Guid seriesId, CancellationToken ct = default)
        {
            if (!Directory.Exists(FolderOf(seriesId))) return SeriesRefusal.UnknownSeries;
            try
            {
                return await LoadRecordAsync(seriesId, ct) is { } series ? series : SeriesRefusal.UnknownSeries;
            }
            catch (Exception ex) when (ex is SqliteException or InvalidOperationException)
            {
                logger.LogWarning(ex, "Series registry {SeriesId} could not be read", seriesId);
                return SeriesRefusal.Unreadable;
            }
        }

        private async Task<SeriesRecord?> LoadRecordAsync(Guid seriesId, CancellationToken ct)
        {
            var folder = FolderOf(seriesId);
            if (!File.Exists(Path.Combine(folder, SeriesDbContext.DbFileName))) return null;
            // Reads take no lock (#246): a series deleted since the check above must not be recreated.
            await using var db = await SeriesDbContext.OpenExistingAsync(folder, ct);
            var row = await db.Series.AsNoTracking().SingleOrDefaultAsync(ct);
            if (row is null) return null;
            var members = await db.Books.AsNoTracking().ToDictionaryAsync(b => b.ProjectId, b => b.Position, ct);
            var links = await db.Links.AsNoTracking()
                .Select(l => new SeriesLinkRules.Link(l.SeriesCharacterId, l.ProjectId, l.CharacterId))
                .ToListAsync(ct);
            var notTheSame = await db.NotTheSame.AsNoTracking()
                .Select(n => new SeriesNotTheSame(n.Id, n.ProjectId, n.CharacterId, n.SeriesCharacterId, n.OtherProjectId, n.OtherCharacterId))
                .ToListAsync(ct);
            var uploaded = PosterFiles(folder).FirstOrDefault() is { } poster
                ? SeriesPoster.Of(SeriesPosterSource.Uploaded, poster, coverPosition: null)
                : null;
            return new SeriesRecord(seriesId, row.Name, members, links, notTheSame, uploaded);
        }

        /// <summary>The uploaded poster files in a series folder, an upload in progress left out.</summary>
        public static IReadOnlyList<string> PosterFiles(string folder) =>
            Directory.Exists(folder)
                ? Directory.GetFiles(folder, SeriesRegistry.PosterName + ".*")
                    .Where(f => !f.EndsWith(UploadSuffix, StringComparison.OrdinalIgnoreCase))
                    .Order(StringComparer.OrdinalIgnoreCase)
                    .ToList()
                : [];

        public static string UploadPath(string folder) => Path.Combine(folder, SeriesRegistry.PosterName + UploadSuffix);

        private static SeriesPoster? CoverOf(string projectFolder, string? coverImage)
        {
            if (coverImage is not { Length: > 0 }) return null;
            var path = Path.Combine(projectFolder, Path.GetFileName(coverImage));
            return File.Exists(path) ? SeriesPoster.Of(SeriesPosterSource.Cover, path, coverPosition: null) : null;
        }

        // ---- the Voices layer --------------------------------------------------------------------

        /// <summary>
        /// <paramref name="series"/> with the Voices layer of these Series characters only: each of
        /// their linked books' Voices (<see cref="SeriesSnapshot.VoicedPeers"/>), reference audio
        /// hashed, positional rules and default Voice. A book whose Voices can't be read is logged and
        /// left out. Every other Series character gets none.
        /// </summary>
        public async Task<SeriesSnapshot> WithVoicesAsync(SeriesSnapshot series, IEnumerable<Guid> seriesCharacterIds, CancellationToken ct = default)
        {
            var voices = new Dictionary<Guid, IReadOnlyList<SeriesHeldVoices>>();
            foreach (var id in seriesCharacterIds.Distinct())
            {
                var held = new List<SeriesHeldVoices>();
                foreach (var book in series.VoicedPeers(id))
                {
                    ct.ThrowIfCancellationRequested();
                    if (await HeldAsync(series.Id, book) is { } h) held.Add(h);
                }
                voices[id] = held;
            }
            return series.WithVoices(voices);
        }

        private async Task<SeriesHeldVoices?> HeldAsync(Guid seriesId, SeriesPeer book)
        {
            try
            {
                var voices = await characters.GetCharacterVoicesAsync(book.FolderId, book.CharacterId);
                var folder = fs.GetProjectFolderPath(book.FolderId);
                var states = new List<SeriesVoiceState>(voices.Count);
                foreach (var v in voices)
                    states.Add(new SeriesVoiceState(v.Id, v.Name, v.Description, v.Source, v.DesignPrompt, v.Transcript,
                        v.VoiceDesignSettingsOverrideJson, v.TtsSettingsOverrideJson, await AudioHashAsync(folder, v.AudioFileName)));
                var rules = (await characters.GetCharacterVoiceRulesAsync(book.FolderId, book.CharacterId))
                    .Where(r => !r.IsDefault).Select(r => r.VoiceId).ToList();
                var defaultVoice = await characters.GetDefaultVoiceIdAsync(book.FolderId, book.CharacterId);
                return new SeriesHeldVoices(book, voices, SeriesVoiceVersion.Of(states), rules, defaultVoice);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Series {SeriesId}: the Voices of '{Folder}' could not be read", seriesId, book.Folder);
                return null;
            }
        }

        /// <summary>The reference audio's content hash; a named file that is missing hashes as such.</summary>
        private async Task<string?> AudioHashAsync(string projectFolder, string? audioFileName)
        {
            if (audioFileName is not { Length: > 0 }) return null;
            var path = Path.Combine(projectFolder, audioFileName);
            return File.Exists(path) ? await audioHashes.HashAsync(path) : "missing";
        }

        // ---- Characters --------------------------------------------------------------------------

        /// <summary>Each member book's Characters and narrator link; a book whose Characters can't be read is logged and left out.</summary>
        private async Task<IReadOnlyDictionary<Guid, SeriesBookCast>> CastsAsync(SeriesSnapshot series, CancellationToken ct)
        {
            var casts = new Dictionary<Guid, SeriesBookCast>();
            foreach (var book in series.Books)
            {
                IReadOnlyList<SeriesCastCharacter> cast;
                try
                {
                    cast = (await characters.GetCharactersWithAliasesAsync(book.FolderId))
                        .Select(c => new SeriesCastCharacter(c.Id, c.Name, c.IsNarrator, c.Aliases.Select(a => a.Name).ToList()))
                        .ToList();
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Series {SeriesId}: the Characters of '{Folder}' could not be read", series.Id, book.Folder);
                    continue;
                }

                bool? narratorLinked;
                try
                {
                    narratorLinked = (await projects.GetNarratorAsync(book.FolderId, ct)).IsLinked;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Series {SeriesId}: the narrator link of '{Folder}' could not be read", series.Id, book.Folder);
                    narratorLinked = null;
                }
                casts[book.ProjectId] = new SeriesBookCast(cast, narratorLinked);
            }
            return casts;
        }
    }
}
