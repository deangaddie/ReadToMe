using Read2Me.Core.Models;
using Read2Me.Services.Events;

namespace Read2Me.Services.Series
{
    /// <summary>
    /// Shared Voice edits (ADR 0018, copy after commit; #213 "Shared Voices"): a Voice of a
    /// series-linked Character is edited through the per-book routes, and once that commits its
    /// whole state is copied into every other linked book. The registry answers who the peers are
    /// and which deletes are refused; <see cref="SeriesVoiceCopier"/> plans the copies and
    /// <see cref="SeriesVoiceReplicator"/> writes them.
    /// </summary>
    public sealed partial class SeriesRegistry
    {
        // ---- reads -------------------------------------------------------------------------------

        /// <summary>
        /// The Character's Series character and the other books linked to it, in Series position
        /// order; null when the Character is linked to none (or its book is in no series).
        /// </summary>
        public async Task<SeriesPeers?> PeersAsync(ProjectFolderId folder, Guid characterId, CancellationToken ct = default)
        {
            using var _ = await gate.EnterAsync(ct);
            return await PeersCoreAsync(folder, characterId, ct);
        }

        /// <summary>
        /// Whether deleting <paramref name="voiceId"/> of a linked Character is refused (#205 item 9):
        /// another book's positional rules use it, or it is the last Voice of another book that holds it.
        /// </summary>
        public async Task<SeriesVoiceDeleteBlock?> VoiceDeleteBlockAsync(SeriesPeers peers, Guid voiceId) =>
            SeriesVoiceDeleteRules.Check(await DeleteUsesAsync(peers.Others, voiceId, VoiceIdsAsync));

        /// <summary>
        /// The inline series Voice editor's read (#213): the Voices of the series copy, each linked
        /// book with its Character and its default, and the Voices whose delete is refused.
        /// </summary>
        public async Task<SeriesResult<SeriesCharacterView>> CharacterAsync(
            Guid seriesId, Guid seriesCharacterId, CancellationToken ct = default)
        {
            using var _ = await gate.EnterAsync(ct);
            var (state, refusal) = await LinkStateAsync(seriesId, ct);
            if (state is null) return refusal!;
            if (state.Links.All(l => l.SeriesCharacterId != seriesCharacterId)) return SeriesRefusal.UnknownSeriesCharacter;

            var held = await HeldAsync(seriesId, LinkedPeers(state, seriesCharacterId));
            if (Choose(held) is not { } choice) return SeriesRefusal.UnknownSeriesCharacter;

            var books = new List<SeriesCharacterBook>();
            foreach (var h in held)
            {
                var name = state.Find(state.Books.First(b => b.Folder == h.Book.Folder).ProjectId, h.Book.CharacterId)!.Name;
                var defaultVoice = await characters.GetDefaultVoiceIdAsync(h.Book.FolderId, h.Book.CharacterId);
                books.Add(new SeriesCharacterBook(
                    h.Book.Folder, h.Book.Title, h.Book.Position, h.Book.CharacterId, name, defaultVoice, choice.DiffersFrom(h.Book.Folder)));
            }

            // The editor shows and edits the series copy; a delete there is judged as if made in that book.
            var copy = held.First(h => h.Book.Folder == choice.SeriesCopyFolder);
            var others = held.Where(h => h != copy).Select(h => h.Book).ToList();
            var idsByBook = held.ToDictionary(h => h.Book.Folder, h => (IReadOnlyCollection<Guid>)h.Voices.Select(v => v.Id).ToList());
            var blocks = new Dictionary<Guid, SeriesVoiceDeleteBlock>();
            foreach (var voice in copy.Voices)
            {
                var uses = await DeleteUsesAsync(others, voice.Id, b => Task.FromResult(idsByBook[b.Folder]));
                if (SeriesVoiceDeleteRules.Check(uses) is { } block)
                    blocks[voice.Id] = block;
            }
            return new SeriesCharacterView(copy.Book.Folder, copy.Book.CharacterId, copy.Voices, books, blocks);
        }

        // ---- writes ------------------------------------------------------------------------------

        /// <summary>
        /// A linked Character was deleted from its book: its link goes, and the Series character too
        /// when no book is linked any more. The other books keep their Voices.
        /// </summary>
        public async Task ForgetCharacterAsync(ProjectFolderId folder, Guid characterId, CancellationToken ct = default)
        {
            using var _ = await gate.EnterAsync(ct);
            if (await PeersCoreAsync(folder, characterId, ct) is not { } peers) return;
            if (await projects.GetProjectAsync(folder) is not { } project) return;
            await using (var db = await Data.Series.SeriesDbContext.OpenAsync(FolderOf(peers.SeriesId), ct))
                await RemoveLinksAsync(db, l => l.ProjectId == project.Id && l.CharacterId == characterId, ct);
            Signal(peers.SeriesId, SeriesChangeKind.Changed);
        }

        /// <summary>A member book committed something the series cast board shows: its series reads again (#213, Hub).</summary>
        public async Task SignalMemberAsync(ProjectFolderId folder, CancellationToken ct = default)
        {
            if (await MembershipOfAsync(folder, ct) is { } series)
                Signal(series.SeriesId, SeriesChangeKind.Changed);
        }

        // ---- internals ---------------------------------------------------------------------------

        private async Task<SeriesPeers?> PeersCoreAsync(ProjectFolderId folder, Guid characterId, CancellationToken ct)
        {
            if (await projects.GetProjectAsync(folder) is not { } project) return null;
            var series = (await LoadAllAsync(ct)).All.FirstOrDefault(s => s.Members.ContainsKey(project.Id));
            if (series is null) return null;
            var links = await LoadLinksAsync(series.Id, ct);
            var mine = links.FirstOrDefault(l => l.ProjectId == project.Id && l.CharacterId == characterId);
            if (mine is null) return null;

            var index = await IndexProjectsAsync();
            var self = new SeriesPeer(folder.Value, index.Title(project.Id), SeriesPositions.Normalize(series.Members[project.Id]), characterId);
            var others = links
                .Where(l => l.SeriesCharacterId == mine.SeriesCharacterId && l.ProjectId != project.Id && series.Members.ContainsKey(l.ProjectId))
                .Select(l => index.ById(l.ProjectId) is { } p
                    ? new SeriesPeer(p.Folder, p.Title, SeriesPositions.Normalize(series.Members[l.ProjectId]), l.CharacterId)
                    : null)
                .OfType<SeriesPeer>()
                .OrderBy(p => p.Position)
                .ToList();
            return new SeriesPeers(series.Id, mine.SeriesCharacterId, self, others);
        }

        /// <summary>
        /// Each book with its positional rules that use the Voice, and what it holds:
        /// <paramref name="voicesOf"/> answers a book's Voice Ids.
        /// </summary>
        private async Task<IReadOnlyList<SeriesVoiceDeleteRules.OtherBook>> DeleteUsesAsync(
            IReadOnlyList<SeriesPeer> books, Guid voiceId, Func<SeriesPeer, Task<IReadOnlyCollection<Guid>>> voicesOf)
        {
            var uses = new List<SeriesVoiceDeleteRules.OtherBook>();
            foreach (var book in books)
            {
                var rules = await characters.GetCharacterVoiceRulesAsync(book.FolderId, book.CharacterId);
                var held = await voicesOf(book);
                uses.Add(new SeriesVoiceDeleteRules.OtherBook(
                    book.Folder, book.Title, rules.Count(r => !r.IsDefault && r.VoiceId == voiceId),
                    held.Contains(voiceId), held.Any(id => id != voiceId)));
            }
            return uses;
        }

        private async Task<IReadOnlyCollection<Guid>> VoiceIdsAsync(SeriesPeer book) =>
            (await characters.GetCharacterVoicesAsync(book.FolderId, book.CharacterId)).Select(v => v.Id).ToList();
    }

    /// <summary>
    /// The inline series Voice editor's read: the series copy's book, Character and Voices, each
    /// linked book in position order, and the refused deletes by Voice.
    /// </summary>
    public sealed record SeriesCharacterView(
        string SeriesCopyFolder,
        Guid SeriesCopyCharacterId,
        IReadOnlyList<Data.Entities.Voice> Voices,
        IReadOnlyList<SeriesCharacterBook> Books,
        IReadOnlyDictionary<Guid, SeriesVoiceDeleteBlock> DeleteBlocks);

    /// <summary>A linked book in the editor's read; <see cref="Differs"/>: its Voices are not the series copy.</summary>
    public sealed record SeriesCharacterBook(
        string Folder, string Title, decimal Position, Guid CharacterId, string CharacterName, Guid? DefaultVoiceId, bool Differs);
}
