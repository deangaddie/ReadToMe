using Read2Me.Core.Models;
using Read2Me.Services.Mutations;

namespace Read2Me.Services.Series
{
    /// <summary>
    /// Every series read the API answers (#247): each loads a snapshot and asks it, with no lock and
    /// no healing. An endpoint that only reads injects this loader; one that writes injects
    /// <see cref="SeriesRegistry"/>, which plans its writes with <see cref="PlanLinkAsync"/> and
    /// <see cref="PlanResolveAsync"/> on a snapshot loaded under the lock.
    /// <para>
    /// Taking no lock, a read doesn't wait behind a series Voice copy; one made while a copy runs may
    /// briefly show a book as differing, and the <c>series</c> signal that ends the copy makes the
    /// client read again.
    /// </para>
    /// </summary>
    public sealed partial class SeriesSnapshotLoader
    {
        // ---- series and memberships --------------------------------------------------------------

        /// <summary>Every readable series, by name; each one's books in position order.</summary>
        public async Task<IReadOnlyList<SeriesView>> ListAsync(CancellationToken ct = default) =>
            (await LoadWorkspaceAsync(ct)).List();

        public async Task<SeriesResult<SeriesView>> GetAsync(Guid seriesId, CancellationToken ct = default)
        {
            var loaded = await LoadSeriesAsync(seriesId, withCasts: false, ct);
            return loaded.Value is { } series ? series.View() : loaded.Refusal!;
        }

        /// <summary>Each member folder's series, for the project reads.</summary>
        public async Task<IReadOnlyDictionary<string, ProjectSeries>> MembershipsAsync(CancellationToken ct = default) =>
            (await LoadWorkspaceAsync(ct)).Memberships();

        /// <summary>One project's series, or null. Reads only that project and the registries, not every project.</summary>
        public async Task<ProjectSeries?> MembershipOfAsync(ProjectFolderId folder, CancellationToken ct = default) =>
            await LoadForProjectAsync(folder, ct) is { } found
                ? found.Workspace.MembershipOf(found.ProjectId)
                : null;

        // ---- the cast board and its Characters ---------------------------------------------------

        /// <summary>
        /// The series cast board: one row per Series character, one cell per book in position order,
        /// and each book's unlinked Characters for the pickers. A link whose Character has gone from
        /// its book is left out, and a Series character left with no link with it.
        /// </summary>
        public async Task<SeriesResult<SeriesCastView>> CastAsync(Guid seriesId, CancellationToken ct = default)
        {
            var loaded = await LoadSeriesAsync(seriesId, withCasts: true, ct);
            if (loaded.Value is not { } series) return loaded.Refusal!;
            return (await WithVoicesAsync(series, series.Links.Select(l => l.SeriesCharacterId), ct)).Cast();
        }

        /// <summary>
        /// The inline series Voice editor's read (#213): the Voices of the Series copy, each linked
        /// book with its Character and its default, and the Voices whose delete is refused.
        /// </summary>
        public async Task<SeriesResult<SeriesCharacterView>> CharacterAsync(
            Guid seriesId, Guid seriesCharacterId, CancellationToken ct = default)
        {
            var loaded = await LoadSeriesAsync(seriesId, withCasts: true, ct);
            if (loaded.Value is not { } series) return loaded.Refusal!;
            if (!series.IsSeriesCharacter(seriesCharacterId)) return SeriesRefusal.UnknownSeriesCharacter;
            return (await WithVoicesAsync(series, [seriesCharacterId], ct)).Character(seriesCharacterId);
        }

        /// <summary>
        /// The series link of each of a book's linked Characters, for the Cast page's roster: empty
        /// when the book is in no series or links nothing.
        /// </summary>
        public async Task<IReadOnlyDictionary<Guid, CharacterSeriesLink>> LinksOfAsync(ProjectFolderId folder, CancellationToken ct = default)
        {
            if (await LoadSeriesOfAsync(folder, withCasts: true, ct) is not ({ } series, var projectId))
                return new Dictionary<Guid, CharacterSeriesLink>();
            var mine = series.Links.Where(l => l.ProjectId == projectId).Select(l => l.SeriesCharacterId);
            return (await WithVoicesAsync(series, mine, ct)).LinksOf(projectId);
        }

        // ---- previews ----------------------------------------------------------------------------

        /// <summary>
        /// What linking <paramref name="characterId"/> of <paramref name="folder"/> would copy: the
        /// names of the Voices this book gains, and of those the other linked books gain. Refused as
        /// the link itself would be.
        /// </summary>
        public async Task<SeriesResult<SeriesLinkPreview>> PreviewLinkAsync(
            Guid seriesId, Guid seriesCharacterId, string folder, Guid characterId, CancellationToken ct = default)
        {
            var loaded = await LoadSeriesAsync(seriesId, withCasts: true, ct);
            if (loaded.Value is not { } series) return loaded.Refusal!;
            var planned = await PlanLinkAsync(series, seriesCharacterId, folder, characterId);
            return planned.Value is { } plan ? SeriesSnapshot.LinkPreview(plan.Merge) : planned.Refusal!;
        }

        /// <summary>
        /// What a push from, or take into, <paramref name="folder"/> would remove, for its confirm: each
        /// target book that loses Voices, with the positional rules on them. Refused as the write would be.
        /// </summary>
        public async Task<SeriesResult<SeriesResolvePreview>> PreviewResolveAsync(
            Guid seriesId, Guid seriesCharacterId, string folder, bool push, CancellationToken ct = default)
        {
            var loaded = await LoadSeriesAsync(seriesId, withCasts: true, ct);
            if (loaded.Value is not { } registryLayer) return loaded.Refusal!;
            var planned = await PlanResolveAsync(registryLayer, seriesCharacterId, folder, push, ct);
            if (planned.Value is not ({ } series, { } plan)) return planned.Refusal!;
            return new SeriesResolvePreview(series.Losses(seriesCharacterId, plan.Source, plan.Targets));
        }

        /// <summary>
        /// What <i>Share and link</i> would copy, for its confirm: the names of the Voices each book
        /// gains, lowest position first. Every book ends up holding every member's Voices.
        /// </summary>
        public async Task<SeriesResult<IReadOnlyList<SeriesBookGains>>> PreviewShareAndLinkAsync(
            Guid seriesId, IReadOnlyList<SeriesMemberRef> members, CancellationToken ct = default)
        {
            var loaded = await LoadSeriesAsync(seriesId, withCasts: true, ct);
            if (loaded.Value is not { } series) return loaded.Refusal!;
            var picked = series.UnlinkedMembers(members);
            if (picked.Value is not { } chosen) return picked.Refusal!;

            var held = new List<(SeriesMemberBook Book, IReadOnlyList<SeriesVoiceCopy> Voices)>();
            foreach (var (book, character) in chosen)
                held.Add((book, (await VoicesOfAsync(book.FolderId, character.CharacterId)).Select(v => v.Voice).ToList()));
            return SeriesSnapshot.ShareAndLinkPreview(held).ToList();
        }

        // ---- plans, for the previews and the writes ----------------------------------------------

        /// <summary>
        /// A link of <paramref name="characterId"/> to <paramref name="seriesCharacterId"/>, checked
        /// against <paramref name="series"/>, with its Voice merge planned from each linked book's Voices.
        /// </summary>
        public async Task<SeriesResult<SeriesLinkPlan>> PlanLinkAsync(
            SeriesSnapshot series, Guid seriesCharacterId, string folder, Guid characterId)
        {
            var checkedBook = series.CheckLink(seriesCharacterId, folder, characterId);
            if (checkedBook.Value is not { } book) return checkedBook.Refusal!;

            var joining = await VoicesOfAsync(book.FolderId, characterId);
            var linkedBooks = new List<SeriesVoiceMerge.BookVoices<SeriesVoiceSource>>();
            foreach (var other in series.Books)
                if (series.LinkOf(seriesCharacterId, other.ProjectId) is { } link)
                    linkedBooks.Add(new(other.Folder, await VoicesOfAsync(other.FolderId, link.CharacterId)));

            return new SeriesLinkPlan(series, book, SeriesVoiceMerge.Make(joining, linkedBooks, v => v.Voice.Id));
        }

        /// <summary>
        /// <paramref name="registryLayer"/> with the Voices layer of the Series character, and the push
        /// or take planned from it.
        /// </summary>
        public async Task<SeriesResult<SeriesPlannedResolve>> PlanResolveAsync(
            SeriesSnapshot registryLayer, Guid seriesCharacterId, string folder, bool push, CancellationToken ct = default)
        {
            var series = await WithVoicesAsync(registryLayer, [seriesCharacterId], ct);
            var planned = series.PlanResolve(seriesCharacterId, folder, push);
            return planned.Value is { } plan ? new SeriesPlannedResolve(series, plan) : planned.Refusal!;
        }

        /// <summary>A book's Voices of one Character, each with where its files are.</summary>
        private async Task<IReadOnlyList<SeriesVoiceSource>> VoicesOfAsync(ProjectFolderId folder, Guid characterId) =>
            (await characters.GetCharacterVoicesAsync(folder, characterId)).Select(v => SeriesVoiceSource.Of(folder, v)).ToList();
    }
}
