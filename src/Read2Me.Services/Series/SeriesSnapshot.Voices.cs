using VoiceEntity = Read2Me.Data.Entities.Voice;

namespace Read2Me.Services.Series
{
    /// <summary>
    /// One linked book's Voices of a Series character, as the Voices layer holds them (#234, #245):
    /// the Voices, their <see cref="Version"/> (<see cref="SeriesVoiceVersion"/>, reference audio by
    /// content hash), the Voice Id of each of the book's positional Voice Rules, and its default Voice.
    /// </summary>
    public sealed record SeriesHeldVoices(
        SeriesPeer Book,
        IReadOnlyList<VoiceEntity> Voices,
        string Version,
        IReadOnlyList<Guid> PositionalRuleVoices,
        Guid? DefaultVoiceId)
    {
        /// <summary>The Ids of the Voices the book holds.</summary>
        public IReadOnlyCollection<Guid> VoiceIds => Voices.Select(v => v.Id).ToList();
    }

    public sealed partial class SeriesSnapshot
    {
        /// <summary>
        /// The Voices layer: for each Series character a call asked about, the linked books whose
        /// Voices could be read. A Series character not asked about has none, so it differs nowhere.
        /// </summary>
        public IReadOnlyDictionary<Guid, IReadOnlyList<SeriesHeldVoices>> Voices { get; }

        /// <summary>This snapshot with the Voices layer of these Series characters (the loader reads it).</summary>
        public SeriesSnapshot WithVoices(IReadOnlyDictionary<Guid, IReadOnlyList<SeriesHeldVoices>> voices) =>
            new(_record, Index, _castsRead ? Casts : null, voices);

        /// <summary>
        /// The linked books whose Voices the Voices layer reads for a Series character:
        /// <see cref="LinkedPeers"/> when the member books' Characters were read, else every linked
        /// member book (the registry layer alone, as the commit hook loads it).
        /// </summary>
        public IReadOnlyList<SeriesPeer> VoicedPeers(Guid seriesCharacterId) =>
            _castsRead
                ? LinkedPeers(seriesCharacterId)
                : Books
                    .Select(b => LinkOf(seriesCharacterId, b.ProjectId) is { } link ? b.AsPeer(link.CharacterId) : null)
                    .OfType<SeriesPeer>()
                    .ToList();

        private IReadOnlyList<SeriesHeldVoices> HeldOf(Guid seriesCharacterId) => Voices.GetValueOrDefault(seriesCharacterId) ?? [];

        /// <summary>Whether the Voices layer holds this linked book's Voices: false when not asked for, or they couldn't be read.</summary>
        public bool VoicesRead(Guid seriesCharacterId, SeriesPeer book) => HeldIn(seriesCharacterId, book.Folder) is not null;

        private SeriesHeldVoices? HeldIn(Guid seriesCharacterId, string folder) =>
            HeldOf(seriesCharacterId).FirstOrDefault(h => string.Equals(h.Book.Folder, folder, StringComparison.OrdinalIgnoreCase));

        // ---- the Series copy and Differs from series -----------------------------------------------

        /// <summary>
        /// The Series copy of a Series character (<see cref="SeriesCopyChoice"/>) over its linked
        /// books whose Voices could be read; null when its Voices layer holds none.
        /// </summary>
        public SeriesCopyChoice.Choice? SeriesCopy(Guid seriesCharacterId) =>
            HeldOf(seriesCharacterId) is { Count: > 0 } held
                ? SeriesCopyChoice.Choose(held.Select(h => new SeriesCopyChoice.Book(h.Book.Folder, h.Book.Position, h.Version)).ToList())
                : null;

        /// <summary>Whether the linked book in <paramref name="folder"/> differs from series.</summary>
        public bool Differs(Guid seriesCharacterId, string folder) => SeriesCopy(seriesCharacterId)?.DiffersFrom(folder) == true;

        /// <summary>A linked book's Voices of the Series character, each with where its files are; empty when not read.</summary>
        public IReadOnlyList<SeriesVoiceSource> VoicesOf(Guid seriesCharacterId, SeriesPeer book) =>
            HeldIn(seriesCharacterId, book.Folder) is { } held
                ? held.Voices.Select(v => SeriesVoiceSource.Of(book.FolderId, v)).ToList()
                : [];

        // ---- resolving a book that differs ---------------------------------------------------------

        /// <summary>
        /// What replacing each target's Voices with <paramref name="source"/>'s set removes: each target
        /// that loses Voices, with the positional rules on them. A target whose Voices weren't read is left out.
        /// </summary>
        public IReadOnlyList<SeriesVoiceLoss> Losses(Guid seriesCharacterId, SeriesPeer source, IEnumerable<SeriesPeer> targets)
        {
            var kept = HeldIn(seriesCharacterId, source.Folder)?.VoiceIds ?? [];
            var losses = new List<SeriesVoiceLoss>();
            foreach (var target in targets)
            {
                if (HeldIn(seriesCharacterId, target.Folder) is not { } held) continue;
                var voices = held.Voices.Select(v => (v.Id, v.Name)).ToList();
                if (SeriesVoiceLosses.Of(target.Folder, target.Title, kept, voices, held.PositionalRuleVoices) is { } loss)
                    losses.Add(loss);
            }
            return losses;
        }

        /// <summary>
        /// The other linked books an edit made in <see cref="SeriesPeers.Self"/> should bring back in
        /// line (#208, 5; #222): when that book holds the Series copy, every book that differs from it
        /// and would lose no positional rules; else none, since a differing book's edit resolves
        /// nothing silently. Nothing asks first here, so a book that would lose positional rules is
        /// left differing for a push or take.
        /// </summary>
        public IReadOnlySet<string> DifferingPeers(SeriesPeers peers)
        {
            var choice = SeriesCopy(peers.SeriesCharacterId);
            if (choice is null || !string.Equals(choice.SeriesCopyFolder, peers.Self.Folder, StringComparison.OrdinalIgnoreCase))
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var differing = peers.Others.Where(p => choice.DiffersFrom(p.Folder)).ToList();
            var losing = Losses(peers.SeriesCharacterId, peers.Self, differing)
                .Where(l => l.RuleCount > 0).Select(l => l.Folder)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            return differing.Select(p => p.Folder).Where(f => !losing.Contains(f)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        // ---- deleting a shared Voice ---------------------------------------------------------------

        /// <summary>
        /// Whether deleting <paramref name="voiceId"/> in <see cref="SeriesPeers.Self"/> is refused
        /// (#205 item 9): another linked book's positional rules use it, or it is the last Voice of
        /// another book that holds it. A book whose Voices weren't read is not counted
        /// (<see cref="VoicesRead"/>: the commit hook fails the delete then).
        /// </summary>
        public SeriesVoiceDeleteBlock? VoiceDeleteBlock(SeriesPeers peers, Guid voiceId) =>
            VoiceDeleteBlock(peers.SeriesCharacterId, peers.Others, voiceId);

        private SeriesVoiceDeleteBlock? VoiceDeleteBlock(Guid seriesCharacterId, IReadOnlyList<SeriesPeer> others, Guid voiceId) =>
            SeriesVoiceDeleteRules.Check(others
                .Select(b => HeldIn(seriesCharacterId, b.Folder))
                .OfType<SeriesHeldVoices>()
                .Select(h => new SeriesVoiceDeleteRules.OtherBook(
                    h.Book.Folder, h.Book.Title,
                    h.PositionalRuleVoices.Count(id => id == voiceId),
                    h.Voices.Any(v => v.Id == voiceId),
                    h.Voices.Any(v => v.Id != voiceId)))
                .ToList());

        // ---- the inline series Voice editor --------------------------------------------------------

        /// <summary>
        /// The inline series Voice editor's read (#213): the Voices of the Series copy, each linked
        /// book with its Character and its default, and the Voices whose delete is refused there. A
        /// delete in the editor is judged as if made in the book holding the Series copy.
        /// </summary>
        public SeriesResult<SeriesCharacterView> Character(Guid seriesCharacterId)
        {
            if (!IsSeriesCharacter(seriesCharacterId) || SeriesCopy(seriesCharacterId) is not { } choice)
                return SeriesRefusal.UnknownSeriesCharacter;

            var held = HeldOf(seriesCharacterId);
            var books = held
                .Select(h => new SeriesCharacterBook(
                    h.Book.Folder, h.Book.Title, h.Book.Position, h.Book.CharacterId,
                    MemberOf(h.Book.Folder) is { } member ? Find(member.ProjectId, h.Book.CharacterId)?.Name ?? "" : "",
                    h.DefaultVoiceId, choice.DiffersFrom(h.Book.Folder)))
                .ToList();

            var copy = held.First(h => string.Equals(h.Book.Folder, choice.SeriesCopyFolder, StringComparison.OrdinalIgnoreCase));
            var others = held.Where(h => h != copy).Select(h => h.Book).ToList();
            var blocks = new Dictionary<Guid, SeriesVoiceDeleteBlock>();
            foreach (var voice in copy.Voices)
                if (VoiceDeleteBlock(seriesCharacterId, others, voice.Id) is { } block)
                    blocks[voice.Id] = block;
            return new SeriesCharacterView(copy.Book.Folder, copy.Book.CharacterId, copy.Voices, books, blocks);
        }
    }

    /// <summary>
    /// The inline series Voice editor's read: the Series copy's book, Character and Voices, each
    /// linked book in position order, and the refused deletes by Voice.
    /// </summary>
    public sealed record SeriesCharacterView(
        string SeriesCopyFolder,
        Guid SeriesCopyCharacterId,
        IReadOnlyList<VoiceEntity> Voices,
        IReadOnlyList<SeriesCharacterBook> Books,
        IReadOnlyDictionary<Guid, SeriesVoiceDeleteBlock> DeleteBlocks);

    /// <summary>A linked book in the editor's read; <see cref="Differs"/>: its Voices are not the Series copy.</summary>
    public sealed record SeriesCharacterBook(
        string Folder, string Title, decimal Position, Guid CharacterId, string CharacterName, Guid? DefaultVoiceId, bool Differs);
}
