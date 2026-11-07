using Microsoft.EntityFrameworkCore;
using Read2Me.Core.Models;
using Read2Me.Data.Series;
using Read2Me.Services.Events;
using Read2Me.Services.History;
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
        /// its book is left out, and a Series character left with no link with it.
        /// </summary>
        public async Task<SeriesResult<SeriesCastView>> CastAsync(Guid seriesId, CancellationToken ct = default)
        {
            using var _ = await gate.EnterAsync(ct);
            var loaded = await LoadHealedAsync(seriesId, ct);
            if (loaded.Value is not { } series) return loaded.Refusal!;
            return series.Cast(await ChoicesAsync(series, series.Links.Select(l => l.SeriesCharacterId)));
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
            var loaded = await LoadHealedAsync(seriesId, ct);
            if (loaded.Value is not { } series) return loaded.Refusal!;
            var (plan, refusal) = await PlanLinkAsync(series, seriesCharacterId, folder, characterId);
            if (plan is null) return refusal!;
            return SeriesSnapshot.LinkPreview(plan.Merge);
        }

        /// <summary>
        /// The series link of each of a book's linked Characters, for the Cast page's roster: empty
        /// when the book is in no series or links nothing.
        /// </summary>
        public async Task<IReadOnlyDictionary<Guid, CharacterSeriesLink>> LinksOfAsync(ProjectFolderId folder, CancellationToken ct = default)
        {
            using var _ = await gate.EnterAsync(ct);
            if (await loader.LoadSeriesOfAsync(folder, withCasts: true, ct) is not { } found)
                return new Dictionary<Guid, CharacterSeriesLink>();
            var (series, projectId) = found;
            var mine = series.Links.Where(l => l.ProjectId == projectId).Select(l => l.SeriesCharacterId);
            return series.LinksOf(projectId, await ChoicesAsync(series, mine));
        }

        // ---- writes ------------------------------------------------------------------------------

        /// <summary>
        /// Share as new: a Series character made from this Character, which then owns its Voices.
        /// Nothing is copied: the only linked book already holds them.
        /// </summary>
        public async Task<SeriesResult<Guid>> ShareAsync(Guid seriesId, string folder, Guid characterId, CancellationToken ct = default)
        {
            using var _ = await gate.EnterAsync(ct);
            var loaded = await LoadHealedAsync(seriesId, ct);
            if (loaded.Value is not { } series) return loaded.Refusal!;
            var checkedBook = series.CheckUnlinked(folder, characterId);
            if (checkedBook.Value is not { } book) return checkedBook.Refusal!;

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
            using var hold = await gate.EnterAsync(ct);
            var loaded = await LoadHealedAsync(seriesId, ct);
            if (loaded.Value is not { } series) return loaded.Refusal!;
            var (plan, refusal) = await PlanLinkAsync(series, seriesCharacterId, folder, characterId);
            if (plan is null) return refusal!;
            if (await RunLinkAsync(seriesId, seriesCharacterId, characterId, plan, hold, ct) is { } failed) return failed;
            Signal(seriesId, SeriesChangeKind.Changed);
            return true;
        }

        /// <summary>A new Series character owning this Character (already checked), whose <i>Not the same</i> go.</summary>
        private async Task<Guid> ShareUncheckedAsync(Guid seriesId, SeriesMemberBook book, Guid characterId, CancellationToken ct)
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
            Guid seriesId, Guid seriesCharacterId, Guid characterId, LinkPlan plan, SeriesRegistryLock.Hold hold, CancellationToken ct)
        {
            var book = plan.Book;
            if (plan.Merge.JoiningGains.Count > 0)
            {
                var joining = new SeriesVoiceWrite(
                    book.AsPeer(characterId),
                    new SeriesVoiceChange.Gain(plan.Merge.JoiningGains.Select(g => g.Voice).ToList()));
                var result = (await replicator.WriteAsync([joining], LinkStep, hold, ct))[0];
                if (result.Failure is { } reason)
                    return SeriesRefusal.VoicesNotWritten(book.Title, reason, result.Rejection == BookMutationRejection.Conflict);
            }

            await using (var db = await SeriesDbContext.OpenAsync(FolderOf(seriesId), ct))
            {
                db.Links.Add(new SeriesLink { SeriesCharacterId = seriesCharacterId, ProjectId = book.ProjectId, CharacterId = characterId });
                ForgetNotTheSame(db, [new(book.ProjectId, characterId)]);
                await db.SaveChangesAsync(ct);
            }

            // The link stands from here: a book that can't take its Voices is logged and the rest go on
            // (it then differs from series, #222).
            var writes = plan.Merge.LinkedGains.Select(gains =>
            {
                var other = plan.Series.Books.First(b => b.Folder == gains.Folder);
                var otherCharacter = plan.Series.LinkOf(seriesCharacterId, other.ProjectId)!.CharacterId;
                return new SeriesVoiceWrite(other.AsPeer(otherCharacter), new SeriesVoiceChange.Gain(gains.Voices));
            }).ToList();
            if (writes.Count > 0) await replicator.WriteAsync(writes, LinkStep, hold, ct);
            return null;
        }

        private static readonly SeriesVoiceStep LinkStep = new(GainSeriesVoicesMutation.LinkLabel, StepAction.Voice);

        /// <summary>Unlink: the book keeps its own copy of the Voices, and edits stop crossing books.</summary>
        public async Task<SeriesResult<bool>> UnlinkAsync(Guid seriesId, Guid seriesCharacterId, string folder, CancellationToken ct = default)
        {
            using var _ = await gate.EnterAsync(ct);
            var loaded = await LoadHealedAsync(seriesId, ct);
            if (loaded.Value is not { } series) return loaded.Refusal!;
            if (series.MemberOf(folder) is not { } book) return series.NotAMember(folder);
            if (!series.IsSeriesCharacter(seriesCharacterId)) return SeriesRefusal.UnknownSeriesCharacter;
            if (series.LinkOf(seriesCharacterId, book.ProjectId) is null)
                return SeriesRefusal.NotLinked(book.Title, series.LabelOf(seriesCharacterId));

            await using (var db = await SeriesDbContext.OpenAsync(FolderOf(seriesId), ct))
                await RemoveLinksAsync(db, l => l.SeriesCharacterId == seriesCharacterId && l.ProjectId == book.ProjectId, ct);
            Signal(seriesId, SeriesChangeKind.Changed);
            return true;
        }

        // ---- internals ---------------------------------------------------------------------------

        private sealed record LinkPlan(SeriesSnapshot Series, SeriesMemberBook Book, SeriesVoiceMerge.Plan<SeriesVoiceSource> Merge);

        /// <summary>
        /// The series copy of each named Series character, over its linked books whose Characters
        /// could be read; a Series character none of whose Voices can be read is left out.
        /// </summary>
        private async Task<IReadOnlyDictionary<Guid, SeriesCopyChoice.Choice>> ChoicesAsync(SeriesSnapshot series, IEnumerable<Guid> seriesCharacterIds)
        {
            var choices = new Dictionary<Guid, SeriesCopyChoice.Choice>();
            foreach (var id in seriesCharacterIds.Distinct())
                if (await ChooseAsync(series.Id, series.LinkedPeers(id)) is { } choice)
                    choices[id] = choice;
            return choices;
        }

        private async Task<(LinkPlan? Plan, SeriesRefusal? Refusal)> PlanLinkAsync(
            SeriesSnapshot series, Guid seriesCharacterId, string folder, Guid characterId)
        {
            var checkedBook = series.CheckLink(seriesCharacterId, folder, characterId);
            if (checkedBook.Value is not { } book) return (null, checkedBook.Refusal);

            var joining = await VoicesAsync(book.FolderId, characterId);
            var linkedBooks = new List<SeriesVoiceMerge.BookVoices<SeriesVoiceSource>>();
            foreach (var other in series.Books)
                if (series.LinkOf(seriesCharacterId, other.ProjectId) is { } link)
                    linkedBooks.Add(new(other.Folder, await VoicesAsync(other.FolderId, link.CharacterId)));

            var merge = SeriesVoiceMerge.Make(joining, linkedBooks, v => v.Voice.Id);
            return (new LinkPlan(series, book, merge), null);
        }

        /// <summary>A book's Voices of one Character, each with where its files are.</summary>
        private async Task<IReadOnlyList<SeriesVoiceSource>> VoicesAsync(ProjectFolderId folder, Guid characterId) =>
            (await characters.GetCharacterVoicesAsync(folder, characterId)).Select(v => SeriesVoiceSource.Of(folder, v)).ToList();

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
