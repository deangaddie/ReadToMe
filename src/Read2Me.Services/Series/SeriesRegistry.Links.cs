using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Read2Me.Core.Models;
using Read2Me.Data.Series;
using Read2Me.Services.Audio;
using Read2Me.Services.Events;
using Read2Me.Services.Mutations;

namespace Read2Me.Services.Series
{
    /// <summary>
    /// Series characters and their links (ADR 0018, Character linking #208): the series cast board's
    /// read, Share as new, Link with its Voice merge, and Unlink. The registry records only which
    /// book's Character is linked to which Series character; every Voice lives in the books, so a
    /// link copies Voices into each book that lacks them, each copy a Book mutation and History step
    /// there, and an unlink copies nothing.
    /// </summary>
    public sealed partial class SeriesRegistry
    {
        // ---- reads -------------------------------------------------------------------------------

        /// <summary>
        /// The series cast board: one row per Series character, one cell per book in position order,
        /// and each book's unlinked Characters for the pickers. A link whose Character has gone from
        /// its book is dropped here, and a Series character left with no link with it.
        /// </summary>
        public async Task<SeriesResult<SeriesCastView>> CastAsync(Guid seriesId, CancellationToken ct = default)
        {
            using var _ = await gate.EnterAsync(ct);
            var (state, refusal) = await LinkStateAsync(seriesId, ct);
            if (state is null) return refusal!;

            var suggestions = await SuggestAsync(state, ct);
            var unordered = new List<SeriesCastRow>();
            foreach (var seriesCharacterId in state.Links.Select(l => l.SeriesCharacterId).Distinct())
            {
                var choice = await ChooseAsync(seriesId, LinkedPeers(state, seriesCharacterId));
                var cells = state.Books.Select(b =>
                {
                    var link = state.LinkOf(seriesCharacterId, b.ProjectId);
                    var suggested = suggestions.Cells
                        .FirstOrDefault(s => s.SeriesCharacterId == seriesCharacterId && s.ProjectId == b.ProjectId);
                    return new SeriesCastCell(
                        b.Folder,
                        link is null ? null : state.Find(link.ProjectId, link.CharacterId),
                        link is not null && choice?.DiffersFrom(b.Folder) == true,
                        suggested is null ? null : state.Find(b.ProjectId, suggested.CharacterId));
                }).ToList();
                unordered.Add(new SeriesCastRow(seriesCharacterId, state.LabelOf(seriesCharacterId), cells.Any(c => c.Linked?.IsNarrator == true), cells));
            }

            var rows = unordered
                .OrderBy(r => r.IsNarrator ? 0 : 1)
                .ThenBy(r => r.Label, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            var unlinked = state.Books.ToDictionary(
                b => b.Folder,
                b => (IReadOnlyList<SeriesCastCharacter>)(state.Characters.GetValueOrDefault(b.ProjectId) ?? [])
                    .Where(c => state.Links.All(l => l.ProjectId != b.ProjectId || l.CharacterId != c.CharacterId))
                    .ToList(),
                StringComparer.OrdinalIgnoreCase);

            var suggestedRows = suggestions.Rows
                .Select(members => new SeriesSuggestedRow(members
                    .Select(m => (Book: state.Books.First(b => b.ProjectId == m.ProjectId), Character: state.Find(m.ProjectId, m.CharacterId)!))
                    .Select(m => new SeriesSuggestedMember(m.Book.Folder, m.Character.CharacterId, m.Character.Name, m.Character.IsNarrator))
                    .ToList()))
                .OrderBy(r => r.Members[0].IsNarrator ? 0 : 1)
                .ThenBy(r => r.Label, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            return new SeriesCastView(
                state.Books.Select(b => new SeriesBookView(b.Folder, b.Title, b.Position)).ToList(),
                rows,
                suggestedRows,
                unlinked,
                suggestions.NameMatches,
                suggestions.Count);
        }

        /// <summary>
        /// What linking <paramref name="characterId"/> of <paramref name="folder"/> would copy: the
        /// names of the Voices this book gains, and of those the other linked books gain. Refused as
        /// the link itself would be.
        /// </summary>
        public async Task<SeriesResult<SeriesLinkPreview>> PreviewLinkAsync(
            Guid seriesId, Guid seriesCharacterId, string folder, Guid characterId, CancellationToken ct = default)
        {
            using var _ = await gate.EnterAsync(ct);
            var (plan, refusal) = await PlanLinkAsync(seriesId, seriesCharacterId, folder, characterId, ct);
            if (plan is null) return refusal!;

            var seriesGains = plan.Merge.LinkedGains.SelectMany(b => b.Voices).DistinctBy(v => v.Voice.Id).Select(v => v.Voice.Name);
            return new SeriesLinkPreview(plan.Merge.JoiningGains.Select(g => g.Voice.Voice.Name).ToList(), seriesGains.ToList());
        }

        /// <summary>
        /// The series link of each of a book's linked Characters, for the Cast page's roster: empty
        /// when the book is in no series or links nothing.
        /// </summary>
        public async Task<IReadOnlyDictionary<Guid, CharacterSeriesLink>> LinksOfAsync(ProjectFolderId folder, CancellationToken ct = default)
        {
            using var _ = await gate.EnterAsync(ct);
            if (await projects.GetProjectAsync(folder) is not { } project) return new Dictionary<Guid, CharacterSeriesLink>();
            var series = (await LoadAllAsync(ct)).All.FirstOrDefault(s => s.Members.ContainsKey(project.Id));
            if (series is null) return new Dictionary<Guid, CharacterSeriesLink>();

            var links = await LoadLinksAsync(series.Id, ct);
            var mine = links.Where(l => l.ProjectId == project.Id).ToList();
            if (mine.Count == 0) return new Dictionary<Guid, CharacterSeriesLink>();

            var index = await IndexProjectsAsync();
            var result = new Dictionary<Guid, CharacterSeriesLink>();
            foreach (var link in mine)
            {
                var peers = links
                    .Where(o => o.SeriesCharacterId == link.SeriesCharacterId && series.Members.ContainsKey(o.ProjectId))
                    .Select(o => index.ById(o.ProjectId) is { } p
                        ? new SeriesPeer(p.Folder, p.Title, SeriesPositions.Normalize(series.Members[o.ProjectId]), o.CharacterId)
                        : null)
                    .OfType<SeriesPeer>()
                    .ToList();
                var choice = await ChooseAsync(series.Id, peers);
                var differs = choice is not null && index.ById(project.Id) is { } self && choice.DiffersFrom(self.Folder);
                result[link.CharacterId] = new CharacterSeriesLink(series.Id, link.SeriesCharacterId, series.Name,
                    links.Count(o => o.SeriesCharacterId == link.SeriesCharacterId && series.Members.ContainsKey(o.ProjectId)), differs);
            }
            return result;
        }

        // ---- writes ------------------------------------------------------------------------------

        /// <summary>
        /// Share as new: a Series character made from this Character, which then owns its Voices.
        /// Nothing is copied: the only linked book already holds them.
        /// </summary>
        public async Task<SeriesResult<Guid>> ShareAsync(Guid seriesId, string folder, Guid characterId, CancellationToken ct = default)
        {
            using var _ = await gate.EnterAsync(ct);
            var (state, refusal) = await LinkStateAsync(seriesId, ct);
            if (state is null) return refusal!;
            if (MemberOf(state, folder) is not { } book) return await NotAMemberAsync(state, folder);
            if (state.Find(book.ProjectId, characterId) is not { } character) return SeriesRefusal.UnknownCharacter(book.Title);
            if (SeriesLinkRules.CheckShare(state.Links, book.ProjectId, characterId) is { } linked)
                return SeriesRefusal.CharacterAlreadyLinked(character.Name, book.Title, state.LabelOf(linked.SeriesCharacterId));

            var id = await ShareUncheckedAsync(seriesId, book, characterId, ct);
            Signal(seriesId, SeriesChangeKind.Changed);
            return id;
        }

        /// <summary>
        /// Links a book's Character to a Series character, running the Voice merge: this book gains
        /// the Voices the linked books hold and it lacks, then each linked book, in Series position
        /// order, gains this Character's Voices it lacks, all under the same Voice Ids. This book's
        /// half fails the link; a later book's failure is logged and leaves the link standing.
        /// </summary>
        public async Task<SeriesResult<bool>> LinkAsync(
            Guid seriesId, Guid seriesCharacterId, string folder, Guid characterId, CancellationToken ct = default)
        {
            using var _ = await gate.EnterAsync(ct);
            var (plan, refusal) = await PlanLinkAsync(seriesId, seriesCharacterId, folder, characterId, ct);
            if (plan is null) return refusal!;
            if (await RunLinkAsync(seriesId, seriesCharacterId, characterId, plan, ct) is { } failed) return failed;
            Signal(seriesId, SeriesChangeKind.Changed);
            return true;
        }

        /// <summary>A new Series character owning this Character (already checked), whose <i>Not the same</i> go.</summary>
        private async Task<Guid> ShareUncheckedAsync(Guid seriesId, LinkBook book, Guid characterId, CancellationToken ct)
        {
            var id = Guid.NewGuid();
            await using var db = await SeriesDbContext.OpenAsync(FolderOf(seriesId), ct);
            db.Characters.Add(new SeriesCharacterRow { Id = id });
            db.Links.Add(new SeriesLink { SeriesCharacterId = id, ProjectId = book.ProjectId, CharacterId = characterId });
            ForgetNotTheSame(db, [new(book.ProjectId, characterId)]);
            await db.SaveChangesAsync(ct);
            return id;
        }

        /// <summary>Runs a planned link; a refusal when the joining book's half didn't commit and nothing was linked.</summary>
        private async Task<SeriesRefusal?> RunLinkAsync(
            Guid seriesId, Guid seriesCharacterId, Guid characterId, LinkPlan plan, CancellationToken ct)
        {
            var book = plan.Book;
            if (plan.Merge.JoiningGains.Count > 0)
            {
                var outcome = await GainAsync(book, characterId, plan.Merge.JoiningGains.Select(g => g.Voice), ct);
                if (outcome is BookMutationOutcome.Rejected rejected)
                    return SeriesRefusal.VoicesNotWritten(book.Title, rejected.Message, rejected.Reason == BookMutationRejection.Conflict);
            }

            await using (var db = await SeriesDbContext.OpenAsync(FolderOf(seriesId), ct))
            {
                db.Links.Add(new SeriesLink { SeriesCharacterId = seriesCharacterId, ProjectId = book.ProjectId, CharacterId = characterId });
                ForgetNotTheSame(db, [new(book.ProjectId, characterId)]);
                await db.SaveChangesAsync(ct);
            }

            // The link stands from here: a book that can't take its Voices is logged and the rest go on
            // (it then differs from series, #222).
            foreach (var gains in plan.Merge.LinkedGains)
            {
                var other = plan.State.Books.First(b => b.Folder == gains.Folder);
                var otherCharacter = plan.State.LinkOf(seriesCharacterId, other.ProjectId)!.CharacterId;
                string? failure;
                try
                {
                    failure = await GainAsync(other, otherCharacter, gains.Voices, ct) is BookMutationOutcome.Rejected rejected
                        ? rejected.Message
                        : null;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Series {SeriesId}: copying Voices into '{Folder}' threw", seriesId, other.Folder);
                    failure = ex.Message;
                }
                if (failure is not null)
                    logger.LogWarning("Series {SeriesId}: linking left '{Folder}' without {Count} Voices: {Reason}",
                        seriesId, other.Folder, gains.Voices.Count, failure);
            }
            return null;
        }

        /// <summary>Unlink: the book keeps its own copy of the Voices, and edits stop crossing books.</summary>
        public async Task<SeriesResult<bool>> UnlinkAsync(Guid seriesId, Guid seriesCharacterId, string folder, CancellationToken ct = default)
        {
            using var _ = await gate.EnterAsync(ct);
            var (state, refusal) = await LinkStateAsync(seriesId, ct);
            if (state is null) return refusal!;
            if (MemberOf(state, folder) is not { } book) return await NotAMemberAsync(state, folder);
            if (state.Links.All(l => l.SeriesCharacterId != seriesCharacterId)) return SeriesRefusal.UnknownSeriesCharacter;
            if (state.LinkOf(seriesCharacterId, book.ProjectId) is null)
                return SeriesRefusal.NotLinked(book.Title, state.LabelOf(seriesCharacterId));

            await using (var db = await SeriesDbContext.OpenAsync(FolderOf(seriesId), ct))
                await RemoveLinksAsync(db, l => l.SeriesCharacterId == seriesCharacterId && l.ProjectId == book.ProjectId, ct);
            Signal(seriesId, SeriesChangeKind.Changed);
            return true;
        }

        // ---- internals ---------------------------------------------------------------------------

        private sealed record LinkBook(string Folder, Guid ProjectId, string Title, decimal Position)
        {
            public ProjectFolderId FolderId => new(Folder);
        }

        /// <summary>A series' member books in position order, their Characters, and its links (healed).</summary>
        private sealed record LinkState(
            Loaded Series,
            IReadOnlyList<LinkBook> Books,
            IReadOnlyDictionary<Guid, IReadOnlyList<SeriesCastCharacter>> Characters,
            IReadOnlyList<SeriesLinkRules.Link> Links)
        {
            public SeriesCastCharacter? Find(Guid projectId, Guid characterId) =>
                Characters.GetValueOrDefault(projectId)?.FirstOrDefault(c => c.CharacterId == characterId);

            /// <summary>The book's link to a Series character, if it has one.</summary>
            public SeriesLinkRules.Link? LinkOf(Guid seriesCharacterId, Guid projectId) =>
                Links.FirstOrDefault(l => l.SeriesCharacterId == seriesCharacterId && l.ProjectId == projectId);

            /// <summary>A row's label: its linked Character's name in the lowest-positioned book (whose Characters could be read).</summary>
            public string LabelOf(Guid seriesCharacterId) =>
                Books.Select(b => LinkOf(seriesCharacterId, b.ProjectId))
                    .Select(l => l is null ? null : Find(l.ProjectId, l.CharacterId))
                    .FirstOrDefault(c => c is not null)?.Name
                    ?? "the series character";
        }

        private sealed record LinkPlan(LinkState State, LinkBook Book, SeriesVoiceMerge.Plan<SourcedVoice> Merge);

        /// <summary>A Voice as one book holds it: where its files are, and the row to copy.</summary>
        private sealed record SourcedVoice(LinkBook Book, Guid CharacterId, SeriesVoiceCopy Voice);

        private static LinkBook? MemberOf(LinkState state, string folder) =>
            state.Books.FirstOrDefault(b => string.Equals(b.Folder, folder, StringComparison.OrdinalIgnoreCase));

        private async Task<SeriesRefusal> NotAMemberAsync(LinkState state, string folder) =>
            (await IndexProjectsAsync()).ByFolder(folder) is { } project
                ? SeriesRefusal.NotAMember(project.Title, state.Series.Name)
                : SeriesRefusal.UnknownFolder(folder);

        private async Task<(LinkState? State, SeriesRefusal? Refusal)> LinkStateAsync(Guid seriesId, CancellationToken ct)
        {
            var (series, refusal) = await FindAsync(seriesId, ct);
            if (series is null) return (null, refusal);
            var index = await IndexProjectsAsync();
            var view = await ViewAsync(series, index, ct);
            var books = view.Books
                .Select(b => new LinkBook(b.Folder, index.ByFolder(b.Folder)!.ProjectId, b.Title, b.Position))
                .ToList();

            var roster = new Dictionary<Guid, IReadOnlyList<SeriesCastCharacter>>();
            foreach (var book in books)
            {
                try
                {
                    roster[book.ProjectId] = (await characters.GetCharactersWithAliasesAsync(book.FolderId))
                        .Select(c => new SeriesCastCharacter(c.Id, c.Name, c.IsNarrator, c.Aliases.Select(a => a.Name).ToList()))
                        .ToList();
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Series {SeriesId}: the Characters of '{Folder}' could not be read", seriesId, book.Folder);
                }
            }

            // A link is dropped only when its book is no longer a member, or was read and no longer has
            // its Character. A member that couldn't be read is still a member (ViewAsync heals only a
            // project that is gone, and its links with it), so its links are kept for a later read.
            var links = await LoadLinksAsync(seriesId, ct);
            bool Gone(SeriesLinkRules.Link l) =>
                !series.Members.ContainsKey(l.ProjectId)
                || (roster.TryGetValue(l.ProjectId, out var cast) && cast.All(c => c.CharacterId != l.CharacterId));
            var gone = links.Where(Gone).ToHashSet();
            if (gone.Count > 0)
            {
                logger.LogInformation("Series {SeriesId}: {Count} links to Characters that are gone are removed", seriesId, gone.Count);
                await using var db = await SeriesDbContext.OpenAsync(FolderOf(seriesId), ct);
                await RemoveLinksAsync(db, gone.Contains, ct);
            }

            // Not the same lasts until its Character is deleted: by the same rule as a link.
            bool Present(Guid projectId, Guid characterId) =>
                series.Members.ContainsKey(projectId)
                && (!roster.TryGetValue(projectId, out var cast) || cast.Any(c => c.CharacterId == characterId));
            await using (var db = await SeriesDbContext.OpenAsync(FolderOf(seriesId), ct))
            {
                var stale = (await db.NotTheSame.ToListAsync(ct))
                    .Where(n => !Present(n.ProjectId, n.CharacterId)
                                || (n.OtherProjectId is { } p && n.OtherCharacterId is { } c && !Present(p, c)))
                    .ToList();
                if (stale.Count > 0)
                {
                    db.NotTheSame.RemoveRange(stale);
                    await db.SaveChangesAsync(ct);
                }
            }
            return (new LinkState(series, books, roster, links.Where(l => !gone.Contains(l)).ToList()), null);
        }

        private async Task<(LinkPlan? Plan, SeriesRefusal? Refusal)> PlanLinkAsync(
            Guid seriesId, Guid seriesCharacterId, string folder, Guid characterId, CancellationToken ct)
        {
            var (state, refusal) = await LinkStateAsync(seriesId, ct);
            if (state is null) return (null, refusal);
            if (MemberOf(state, folder) is not { } book) return (null, await NotAMemberAsync(state, folder));
            if (state.Links.All(l => l.SeriesCharacterId != seriesCharacterId)) return (null, SeriesRefusal.UnknownSeriesCharacter);
            if (state.Find(book.ProjectId, characterId) is not { } character) return (null, SeriesRefusal.UnknownCharacter(book.Title));

            switch (SeriesLinkRules.Check(state.Links, seriesCharacterId, book.ProjectId, characterId))
            {
                case SeriesLinkRules.CharacterAlreadyLinked linked:
                    return (null, SeriesRefusal.CharacterAlreadyLinked(character.Name, book.Title, state.LabelOf(linked.SeriesCharacterId)));
                case SeriesLinkRules.BookAlreadyLinked held:
                    return (null, SeriesRefusal.BookAlreadyLinked(
                        book.Title, state.Find(book.ProjectId, held.CharacterId)!.Name, state.LabelOf(seriesCharacterId)));
            }

            var joining = await VoicesAsync(book, characterId);
            var linkedBooks = new List<SeriesVoiceMerge.BookVoices<SourcedVoice>>();
            foreach (var other in state.Books)
                if (state.LinkOf(seriesCharacterId, other.ProjectId) is { } link)
                    linkedBooks.Add(new(other.Folder, await VoicesAsync(other, link.CharacterId)));

            var merge = SeriesVoiceMerge.Make(joining, linkedBooks, v => v.Voice.Id);
            return (new LinkPlan(state, book, merge), null);
        }

        private async Task<IReadOnlyList<SourcedVoice>> VoicesAsync(LinkBook book, Guid characterId) =>
            (await characters.GetCharacterVoicesAsync(book.FolderId, characterId))
                .Select(v => new SourcedVoice(book, characterId, new SeriesVoiceCopy(
                    v.Id, v.Name, v.Description, v.Source, v.DesignPrompt, v.Transcript, v.AudioFileName,
                    v.VoiceDesignSettingsOverrideJson, v.TtsSettingsOverrideJson, v.CreatedUtc)))
                .ToList();

        /// <summary>
        /// Copies each Voice's reference audio and stored original into <paramref name="target"/>
        /// under its Character's folder, then commits the rows (ADR 0007: the files exist before the
        /// Book names them). A copy the commit doesn't take is removed again.
        /// </summary>
        private async Task<BookMutationOutcome> GainAsync(
            LinkBook target, Guid characterId, IEnumerable<SourcedVoice> voices, CancellationToken ct)
        {
            var targetFolder = fs.GetProjectFolderPath(target.FolderId);
            var written = new List<string>();
            var copies = new List<SeriesVoiceCopy>();
            try
            {
                foreach (var source in voices)
                {
                    var sourceFolder = fs.GetProjectFolderPath(source.Book.FolderId);
                    string? audio = null;
                    if (source.Voice.AudioFileName is { Length: > 0 } from
                        && File.Exists(Path.Combine(sourceFolder, from)))
                    {
                        audio = $"voices/{characterId}/{Path.GetFileName(from)}";
                        Copy(Path.Combine(sourceFolder, from), Path.Combine(targetFolder, audio), written);

                        var original = Path.Combine(sourceFolder, VoiceOriginalStore.PathOf(source.CharacterId, source.Voice.Id));
                        if (File.Exists(original))
                            Copy(original, Path.Combine(targetFolder, VoiceOriginalStore.PathOf(characterId, source.Voice.Id)), written);
                    }
                    copies.Add(source.Voice with { AudioFileName = audio });
                }

                var outcome = await mutations.CommitAsync(new GainSeriesVoicesMutation(target.FolderId, characterId, copies), ct);
                if (outcome is not BookMutationOutcome.Committed)
                    Discard(written);
                return outcome;
            }
            catch
            {
                Discard(written);
                throw;
            }
        }

        /// <summary>Copies a file to a path nothing holds yet; one already there is left alone.</summary>
        private static void Copy(string from, string to, List<string> written)
        {
            if (File.Exists(to)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(from, to);
            written.Add(to);
        }

        private void Discard(IEnumerable<string> written)
        {
            foreach (var file in written)
            {
                try
                {
                    File.Delete(file);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Could not remove the copied Voice file '{File}'.", file);
                }
            }
        }

        private async Task<IReadOnlyList<SeriesLinkRules.Link>> LoadLinksAsync(Guid seriesId, CancellationToken ct)
        {
            await using var db = await SeriesDbContext.OpenAsync(FolderOf(seriesId), ct);
            return await db.Links.AsNoTracking()
                .Select(l => new SeriesLinkRules.Link(l.SeriesCharacterId, l.ProjectId, l.CharacterId))
                .ToListAsync(ct);
        }

        /// <summary>Removes the links <paramref name="removed"/> picks, and every Series character left with none.</summary>
        private static async Task RemoveLinksAsync(
            SeriesDbContext db, Func<SeriesLinkRules.Link, bool> removed, CancellationToken ct)
        {
            var links = await db.Links.ToListAsync(ct);
            var asRules = links.Select(l => new SeriesLinkRules.Link(l.SeriesCharacterId, l.ProjectId, l.CharacterId)).ToList();
            var orphaned = SeriesLinkRules.OrphanedBy(asRules, removed).ToHashSet();
            db.Links.RemoveRange(links.Where((_, i) => removed(asRules[i])));
            var characters = await db.Characters.ToListAsync(ct);
            db.Characters.RemoveRange(characters.Where(c => orphaned.Contains(c.Id) || asRules.All(l => l.SeriesCharacterId != c.Id)));
            await db.SaveChangesAsync(ct);
        }
    }

    /// <summary>
    /// The series cast board (#208, variant B): books in position order, one row per Series character,
    /// and its Link suggestions (#212): <see cref="SuggestedRows"/>, the Series characters each
    /// unlinked Character's names match (<see cref="NameMatches"/>, by Character Id) and
    /// <see cref="SuggestionCount"/>, suggested cells and rows together.
    /// </summary>
    public sealed record SeriesCastView(
        IReadOnlyList<SeriesBookView> Books,
        IReadOnlyList<SeriesCastRow> Rows,
        IReadOnlyList<SeriesSuggestedRow> SuggestedRows,
        IReadOnlyDictionary<string, IReadOnlyList<SeriesCastCharacter>> Unlinked,
        IReadOnlyDictionary<Guid, IReadOnlyList<Guid>> NameMatches,
        int SuggestionCount);

    /// <summary>A Series character's row: its live label and one cell per book, in the board's book order.</summary>
    public sealed record SeriesCastRow(Guid SeriesCharacterId, string Label, bool IsNarrator, IReadOnlyList<SeriesCastCell> Cells);

    /// <summary>
    /// One book's cell in a row: the Character linked there, whether its Voices differ from series,
    /// or else the unlinked Character suggested for it (#212), if any.
    /// </summary>
    public sealed record SeriesCastCell(string Folder, SeriesCastCharacter? Linked, bool Differs, SeriesCastCharacter? Suggested = null);

    /// <summary>A suggested row (#212): unlinked Characters of two or more books, lowest position first. Its label is the first one's name.</summary>
    public sealed record SeriesSuggestedRow(IReadOnlyList<SeriesSuggestedMember> Members)
    {
        public string Label => Members[0].Name;
    }

    public sealed record SeriesSuggestedMember(string Folder, Guid CharacterId, string Name, bool IsNarrator);

    /// <summary>
    /// A book's Character as the board shows it; <see cref="IsNarrator"/> marks the seed Narrator row.
    /// <see cref="Aliases"/> feed the Link suggestions only.
    /// </summary>
    public sealed record SeriesCastCharacter(Guid CharacterId, string Name, bool IsNarrator, IReadOnlyList<string> Aliases);

    /// <summary>The Link confirm's gains: Voice names this book gains, and the other linked books gain.</summary>
    public sealed record SeriesLinkPreview(IReadOnlyList<string> BookGains, IReadOnlyList<string> SeriesGains);

    /// <summary>A Character's series link, as the Cast page's roster shows it.</summary>
    public sealed record CharacterSeriesLink(Guid SeriesId, Guid SeriesCharacterId, string SeriesName, int BookCount, bool Differs);
}
