using Read2Me.Core.Models;
using Read2Me.Services.Mutations;
using static Read2Me.Services.Series.SeriesNameMatch;

namespace Read2Me.Services.Series
{
    /// <summary>
    /// One series' registry layer as <c>series.db</c> stores it (ADR 0018), unfiltered: its members
    /// by project Id with their Series positions, its links, its <i>Not the same</i> rows, and its
    /// uploaded poster, if any.
    /// </summary>
    public sealed record SeriesRecord(
        Guid Id,
        string Name,
        IReadOnlyDictionary<Guid, decimal> Members,
        IReadOnlyList<SeriesLinkRules.Link> Links,
        IReadOnlyList<SeriesNotTheSame> NotTheSame,
        SeriesPoster? UploadedPoster = null);

    /// <summary>
    /// A kept <i>Not the same</i>: on a suggested cell (<see cref="SeriesCharacterId"/> set), or
    /// between two books' Characters from a suggested row (<see cref="OtherProjectId"/> and
    /// <see cref="OtherCharacterId"/> set).
    /// </summary>
    public sealed record SeriesNotTheSame(
        Guid Id, Guid ProjectId, Guid CharacterId, Guid? SeriesCharacterId = null, Guid? OtherProjectId = null, Guid? OtherCharacterId = null)
    {
        public bool IsPair => OtherProjectId is not null && OtherCharacterId is not null;

        public DismissedPair Pair => new(new Member(ProjectId, CharacterId), new Member(OtherProjectId!.Value, OtherCharacterId!.Value));
    }

    /// <summary>A project folder with its Id, title, and its cover as a fallback poster (null: no cover file).</summary>
    public sealed record SeriesProject(string Folder, Guid ProjectId, string Title, SeriesPoster? Cover = null);

    /// <summary>
    /// The project folders a snapshot knows. <see cref="Complete"/> is false when a folder's project.db
    /// is there but could not be read, or when only some projects were indexed: a member missing from
    /// the index may then only be unreadable, not gone.
    /// </summary>
    public sealed class SeriesProjectIndex(IReadOnlyList<SeriesProject> entries, bool complete)
    {
        private readonly Dictionary<Guid, SeriesProject> _byId = entries
            .GroupBy(e => e.ProjectId).ToDictionary(g => g.Key, g => g.First());

        public bool Complete => complete;
        public SeriesProject? ById(Guid id) => _byId.GetValueOrDefault(id);
        public SeriesProject? ByFolder(string folder) =>
            entries.FirstOrDefault(e => string.Equals(e.Folder, folder, StringComparison.OrdinalIgnoreCase));
        public string Title(Guid id) => ById(id)?.Title ?? id.ToString();
        public string TitleOf(string folder) => ByFolder(folder)?.Title ?? folder;
    }

    /// <summary>A member book's Characters; <see cref="NarratorLinked"/> null: its narrator link could not be read.</summary>
    public sealed record SeriesBookCast(IReadOnlyList<SeriesCastCharacter> Characters, bool? NarratorLinked);

    /// <summary>A member book whose project could be read, at its Series position.</summary>
    public sealed record SeriesMemberBook(string Folder, Guid ProjectId, string Title, decimal Position)
    {
        public ProjectFolderId FolderId => new(Folder);
        public SeriesPeer AsPeer(Guid characterId) => new(Folder, Title, Position, characterId);
    }

    /// <summary>A push or take: the book whose Voices are copied, the books they replace, and the step label.</summary>
    public sealed record SeriesResolvePlan(SeriesPeer Source, IReadOnlyList<SeriesPeer> Targets, string Step, IReadOnlyList<SeriesPeer> Linked);

    /// <summary>
    /// One series as every read sees it (#234): the registry layer (members, Series positions, links,
    /// <i>Not the same</i> and the project index), when loaded each member book's Characters, and,
    /// for the Series characters a call names, the Voices layer (<see cref="WithVoices"/>). Every view
    /// is a pure function of it.
    /// <para>
    /// Stale rows are filtered out and never deleted here: a member whose project is gone, a link
    /// whose book is no longer a member or whose Character is gone, and a <i>Not the same</i> whose
    /// Character is gone or now linked. <see cref="StaleMembers"/>, <see cref="StaleLinks"/> and
    /// <see cref="StaleNotTheSame"/> name them for a write to heal.
    /// </para>
    /// <para>
    /// A Character is judged gone only from a book whose Characters were read; a member is judged
    /// gone only when the index is <see cref="SeriesProjectIndex.Complete"/>.
    /// </para>
    /// </summary>
    public sealed partial class SeriesSnapshot
    {
        private readonly SeriesRecord _record;
        private readonly bool _castsRead;

        public SeriesSnapshot(
            SeriesRecord record,
            SeriesProjectIndex index,
            IReadOnlyDictionary<Guid, SeriesBookCast>? casts = null,
            IReadOnlyDictionary<Guid, IReadOnlyList<SeriesHeldVoices>>? voices = null)
        {
            _record = record;
            Index = index;
            _castsRead = casts is not null;
            casts ??= new Dictionary<Guid, SeriesBookCast>();
            Voices = voices ?? new Dictionary<Guid, IReadOnlyList<SeriesHeldVoices>>();

            StaleMembers = index.Complete
                ? record.Members.Keys.Where(id => index.ById(id) is null).ToList()
                : [];
            var live = record.Members.Keys.Except(StaleMembers).ToHashSet();
            Members = record.Members.Where(m => live.Contains(m.Key)).ToDictionary(m => m.Key, m => m.Value);

            Books = live
                .Select(id => index.ById(id) is { } p ? new SeriesMemberBook(p.Folder, p.ProjectId, p.Title, SeriesPositions.Normalize(record.Members[id])) : null)
                .OfType<SeriesMemberBook>()
                .OrderBy(b => b.Position)
                .ToList();
            Casts = Books
                .Where(b => casts.ContainsKey(b.ProjectId))
                .ToDictionary(b => b.ProjectId, b => casts[b.ProjectId]);

            // A member that couldn't be read is still a member, so its links are kept for a later read.
            bool Present(Guid projectId, Guid characterId) =>
                live.Contains(projectId)
                && (!Casts.TryGetValue(projectId, out var cast) || cast.Characters.Any(c => c.CharacterId == characterId));

            StaleLinks = record.Links.Where(l => !Present(l.ProjectId, l.CharacterId)).ToList();
            var stale = StaleLinks.ToHashSet();
            Links = record.Links.Where(l => !stale.Contains(l)).ToList();

            // Not the same lasts until its Character is linked or deleted.
            bool Linked(Guid projectId, Guid characterId) => Links.Any(l => l.ProjectId == projectId && l.CharacterId == characterId);
            bool Kept(Guid projectId, Guid characterId) => Present(projectId, characterId) && !Linked(projectId, characterId);
            StaleNotTheSame = record.NotTheSame
                .Where(n => !Kept(n.ProjectId, n.CharacterId) || (n.IsPair && !Kept(n.OtherProjectId!.Value, n.OtherCharacterId!.Value)))
                .ToList();
            NotTheSame = record.NotTheSame.Except(StaleNotTheSame).ToList();
        }

        public Guid Id => _record.Id;
        public string Name => _record.Name;
        public SeriesProjectIndex Index { get; }

        /// <summary>Every member that is not gone, readable or not, with its stored position.</summary>
        public IReadOnlyDictionary<Guid, decimal> Members { get; }

        /// <summary>The member books that could be read, in Series position order.</summary>
        public IReadOnlyList<SeriesMemberBook> Books { get; }

        /// <summary>The Characters of each member book whose Characters were read, by project Id.</summary>
        public IReadOnlyDictionary<Guid, SeriesBookCast> Casts { get; }

        public IReadOnlyList<SeriesLinkRules.Link> Links { get; }
        public IReadOnlyList<SeriesNotTheSame> NotTheSame { get; }

        public IReadOnlyList<Guid> StaleMembers { get; }
        public IReadOnlyList<SeriesLinkRules.Link> StaleLinks { get; }
        public IReadOnlyList<SeriesNotTheSame> StaleNotTheSame { get; }

        public bool HasStaleRows => StaleMembers.Count > 0 || StaleLinks.Count > 0 || StaleNotTheSame.Count > 0;

        // ---- the series ----------------------------------------------------------------------------

        /// <summary>
        /// The series as the wire shows it. Its poster: the uploaded one; else the cover of the
        /// lowest-positioned book; else none. Only that one book's cover counts.
        /// </summary>
        public SeriesView View()
        {
            var books = Books.Select(b => new SeriesBookView(b.Folder, b.Title, b.Position)).ToList();
            var poster = _record.UploadedPoster
                ?? (Books.FirstOrDefault() is { } first && Index.ById(first.ProjectId)?.Cover is { } cover
                    ? cover with { CoverPosition = first.Position }
                    : SeriesPoster.None);
            return new SeriesView(Id, Name, books, poster);
        }

        /// <summary>The series this project is in, at its position; null when it is not a member.</summary>
        public ProjectSeries? MembershipOf(Guid projectId) =>
            Members.TryGetValue(projectId, out var position)
                ? new ProjectSeries(Id, Name, SeriesPositions.Normalize(position))
                : null;

        public SeriesMemberBook? MemberOf(string folder) =>
            Books.FirstOrDefault(b => string.Equals(b.Folder, folder, StringComparison.OrdinalIgnoreCase));

        /// <summary>A folder that is not a member book: a project not in this series, or no project at all.</summary>
        public SeriesRefusal NotAMember(string folder) =>
            Index.ByFolder(folder) is { } project
                ? SeriesRefusal.NotAMember(project.Title, Name)
                : SeriesRefusal.UnknownFolder(folder);

        // ---- Characters and links ------------------------------------------------------------------

        public SeriesCastCharacter? Find(Guid projectId, Guid characterId) =>
            Casts.GetValueOrDefault(projectId)?.Characters.FirstOrDefault(c => c.CharacterId == characterId);

        public bool IsSeriesCharacter(Guid seriesCharacterId) => Links.Any(l => l.SeriesCharacterId == seriesCharacterId);

        /// <summary>The book's link to a Series character, if it has one.</summary>
        public SeriesLinkRules.Link? LinkOf(Guid seriesCharacterId, Guid projectId) =>
            Links.FirstOrDefault(l => l.SeriesCharacterId == seriesCharacterId && l.ProjectId == projectId);

        /// <summary>A row's label: its linked Character's name in the lowest-positioned book whose Characters could be read.</summary>
        public string LabelOf(Guid seriesCharacterId) =>
            Books.Select(b => LinkOf(seriesCharacterId, b.ProjectId))
                .Select(l => l is null ? null : Find(l.ProjectId, l.CharacterId))
                .FirstOrDefault(c => c is not null)?.Name
                ?? "the series character";

        /// <summary>Each book linked to the Series character whose Character could be read, in Series position order.</summary>
        public IReadOnlyList<SeriesPeer> LinkedPeers(Guid seriesCharacterId) =>
            Books
                .Select(b => LinkOf(seriesCharacterId, b.ProjectId) is { } link && Find(b.ProjectId, link.CharacterId) is not null
                    ? b.AsPeer(link.CharacterId)
                    : null)
                .OfType<SeriesPeer>()
                .ToList();

        /// <summary>
        /// The Character's Series character and the other books linked to it, in Series position
        /// order; null when the Character is linked to none. Needs only the registry layer.
        /// </summary>
        public SeriesPeers? Peers(Guid projectId, Guid characterId)
        {
            if (!Members.TryGetValue(projectId, out var position) || Index.ById(projectId) is not { } project) return null;
            if (Links.FirstOrDefault(l => l.ProjectId == projectId && l.CharacterId == characterId) is not { } mine) return null;

            var self = new SeriesPeer(project.Folder, project.Title, SeriesPositions.Normalize(position), characterId);
            var others = Links
                .Where(l => l.SeriesCharacterId == mine.SeriesCharacterId && l.ProjectId != projectId)
                .Select(l => Books.FirstOrDefault(b => b.ProjectId == l.ProjectId)?.AsPeer(l.CharacterId))
                .OfType<SeriesPeer>()
                .OrderBy(p => p.Position)
                .ToList();
            return new SeriesPeers(Id, mine.SeriesCharacterId, self, others);
        }

        /// <summary>
        /// Whether series.db names the Character, in a link or a <i>Not the same</i>, stale rows
        /// included: its delete then has rows to forget. Needs only the registry layer.
        /// </summary>
        public bool Remembers(Guid projectId, Guid characterId) =>
            _record.Links.Any(l => l.ProjectId == projectId && l.CharacterId == characterId)
            || _record.NotTheSame.Any(n => (n.ProjectId == projectId && n.CharacterId == characterId)
                                           || (n.OtherProjectId == projectId && n.OtherCharacterId == characterId));

        /// <summary>
        /// The series link of each of a book's linked Characters, for the Cast page's roster. It
        /// differs from series only where the Voices layer of its Series character is loaded.
        /// </summary>
        public IReadOnlyDictionary<Guid, CharacterSeriesLink> LinksOf(Guid projectId)
        {
            var self = Index.ById(projectId)?.Folder;
            return Links
                .Where(l => l.ProjectId == projectId)
                .ToDictionary(
                    l => l.CharacterId,
                    l => new CharacterSeriesLink(Id, l.SeriesCharacterId, Name,
                        Links.Count(o => o.SeriesCharacterId == l.SeriesCharacterId),
                        self is not null && Differs(l.SeriesCharacterId, self)));
        }

        // ---- the cast board ------------------------------------------------------------------------

        /// <summary>The board's Link suggestions from the current names and aliases, each book's narrator link, and the kept <i>Not the same</i>.</summary>
        public Suggestions Suggestions()
        {
            var books = new List<Book>();
            foreach (var book in Books)
            {
                // A book whose Characters or narrator link couldn't be read offers nothing, and takes no suggestion.
                if (!Casts.TryGetValue(book.ProjectId, out var cast) || cast.NarratorLinked is not { } narratorLinked) continue;
                books.Add(new Book(book.ProjectId, narratorLinked, cast.Characters
                    .Select(c => new Character(
                        c.CharacterId, c.Name, c.Aliases, c.IsNarrator,
                        Links.FirstOrDefault(l => l.ProjectId == book.ProjectId && l.CharacterId == c.CharacterId)?.SeriesCharacterId))
                    .ToList()));
            }

            var notTheSame = new NotTheSame(
                NotTheSame.Where(n => n.SeriesCharacterId is not null)
                    .Select(n => new DismissedCell(n.SeriesCharacterId!.Value, n.ProjectId, n.CharacterId))
                    .ToList(),
                NotTheSame.Where(n => n.IsPair).Select(n => n.Pair).ToList());
            return Suggest(books, notTheSame);
        }

        /// <summary>
        /// The series cast board: one row per Series character, one cell per book in position order,
        /// and each book's unlinked Characters for the pickers. A cell differs from series only where
        /// the Voices layer of its Series character is loaded.
        /// </summary>
        public SeriesCastView Cast()
        {
            var suggestions = Suggestions();
            var rows = Links.Select(l => l.SeriesCharacterId).Distinct()
                .Select(seriesCharacterId =>
                {
                    var choice = SeriesCopy(seriesCharacterId);
                    var cells = Books.Select(b =>
                    {
                        var link = LinkOf(seriesCharacterId, b.ProjectId);
                        var suggested = suggestions.Cells
                            .FirstOrDefault(s => s.SeriesCharacterId == seriesCharacterId && s.ProjectId == b.ProjectId);
                        return new SeriesCastCell(
                            b.Folder,
                            link is null ? null : Find(link.ProjectId, link.CharacterId),
                            link is not null && choice?.DiffersFrom(b.Folder) == true,
                            suggested is null ? null : Find(b.ProjectId, suggested.CharacterId));
                    }).ToList();
                    return new SeriesCastRow(seriesCharacterId, LabelOf(seriesCharacterId), cells.Any(c => c.Linked?.IsNarrator == true), cells);
                })
                .OrderBy(r => r.IsNarrator ? 0 : 1)
                .ThenBy(r => r.Label, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            var unlinked = Books.ToDictionary(
                b => b.Folder,
                b => (IReadOnlyList<SeriesCastCharacter>)(Casts.GetValueOrDefault(b.ProjectId)?.Characters ?? [])
                    .Where(c => Links.All(l => l.ProjectId != b.ProjectId || l.CharacterId != c.CharacterId))
                    .ToList(),
                StringComparer.OrdinalIgnoreCase);

            var suggestedRows = suggestions.Rows
                .Select(members => new SeriesSuggestedRow(members
                    .Select(m => (Book: Books.First(b => b.ProjectId == m.ProjectId), Character: Find(m.ProjectId, m.CharacterId)!))
                    .Select(m => new SeriesSuggestedMember(m.Book.Folder, m.Character.CharacterId, m.Character.Name, m.Character.IsNarrator))
                    .ToList()))
                .OrderBy(r => r.Members[0].IsNarrator ? 0 : 1)
                .ThenBy(r => r.Label, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            return new SeriesCastView(
                Books.Select(b => new SeriesBookView(b.Folder, b.Title, b.Position)).ToList(),
                rows,
                suggestedRows,
                unlinked,
                suggestions.NameMatches,
                suggestions.Count);
        }

        // ---- checks for the link writes and their previews -----------------------------------------

        /// <summary>Whether <paramref name="characterId"/> of <paramref name="folder"/> may link to the Series character: the book, or why not.</summary>
        public SeriesResult<SeriesMemberBook> CheckLink(Guid seriesCharacterId, string folder, Guid characterId)
        {
            if (MemberOf(folder) is not { } book) return NotAMember(folder);
            if (!IsSeriesCharacter(seriesCharacterId)) return SeriesRefusal.UnknownSeriesCharacter;
            if (Find(book.ProjectId, characterId) is not { } character) return SeriesRefusal.UnknownCharacter(book.Title);

            return SeriesLinkRules.Check(Links, seriesCharacterId, book.ProjectId, characterId) switch
            {
                SeriesLinkRules.CharacterAlreadyLinked linked =>
                    SeriesRefusal.CharacterAlreadyLinked(character.Name, book.Title, LabelOf(linked.SeriesCharacterId)),
                SeriesLinkRules.BookAlreadyLinked held =>
                    SeriesRefusal.BookAlreadyLinked(book.Title, Find(book.ProjectId, held.CharacterId)!.Name, LabelOf(seriesCharacterId)),
                _ => book,
            };
        }

        /// <summary>Whether <paramref name="characterId"/> of <paramref name="folder"/> is an unlinked Character of a member book: the book, or why not.</summary>
        public SeriesResult<SeriesMemberBook> CheckUnlinked(string folder, Guid characterId)
        {
            if (MemberOf(folder) is not { } book) return NotAMember(folder);
            if (Find(book.ProjectId, characterId) is not { } character) return SeriesRefusal.UnknownCharacter(book.Title);
            if (SeriesLinkRules.CheckShare(Links, book.ProjectId, characterId) is { } linked)
                return SeriesRefusal.CharacterAlreadyLinked(character.Name, book.Title, LabelOf(linked.SeriesCharacterId));
            return book;
        }

        /// <summary>
        /// A suggested row's members, checked and in position order: two or more, one per book, each
        /// an unlinked Character of a member book.
        /// </summary>
        public SeriesResult<IReadOnlyList<(SeriesMemberBook Book, SeriesCastCharacter Character)>> UnlinkedMembers(
            IReadOnlyList<SeriesMemberRef> members)
        {
            if (members.Count < 2) return SeriesRefusal.TooFewMembers;
            var picked = new List<(SeriesMemberBook Book, SeriesCastCharacter Character)>();
            foreach (var member in members)
            {
                if (MemberOf(member.Folder) is not { } book) return NotAMember(member.Folder);
                if (picked.Any(p => p.Book == book)) return SeriesRefusal.BookTwice(book.Title);
                if (Find(book.ProjectId, member.CharacterId) is not { } character) return SeriesRefusal.UnknownCharacter(book.Title);
                if (SeriesLinkRules.CheckShare(Links, book.ProjectId, member.CharacterId) is { } linked)
                    return SeriesRefusal.CharacterAlreadyLinked(character.Name, book.Title, LabelOf(linked.SeriesCharacterId));
                picked.Add((book, character));
            }
            return picked.OrderBy(p => p.Book.Position).ToList();
        }

        /// <summary>
        /// A push from, or take into, <paramref name="folder"/>, or why not. It is judged by the
        /// Series copy, so it needs the Voices layer of the Series character.
        /// </summary>
        public SeriesResult<SeriesResolvePlan> PlanResolve(Guid seriesCharacterId, string folder, bool push)
        {
            if (MemberOf(folder) is not { } book) return NotAMember(folder);
            if (!IsSeriesCharacter(seriesCharacterId)) return SeriesRefusal.UnknownSeriesCharacter;
            var label = LabelOf(seriesCharacterId);
            if (LinkOf(seriesCharacterId, book.ProjectId) is null) return SeriesRefusal.NotLinked(book.Title, label);

            var linked = LinkedPeers(seriesCharacterId);
            var choice = SeriesCopy(seriesCharacterId);
            if (choice is null || !choice.DiffersFrom(book.Folder)) return SeriesRefusal.DoesNotDiffer(book.Title, label);

            var self = linked.First(p => p.Folder == book.Folder);
            return push
                ? new SeriesResolvePlan(self, linked.Where(p => p != self).ToList(), SeriesRegistry.PushLabel, linked)
                : new SeriesResolvePlan(linked.First(p => p.Folder == choice.SeriesCopyFolder), [self], SeriesRegistry.TakeLabel, linked);
        }

        // ---- previews ------------------------------------------------------------------------------

        /// <summary>The Link confirm: the Voice names the joining book gains, and those the other linked books gain.</summary>
        public static SeriesLinkPreview LinkPreview(SeriesVoiceMerge.Plan<SeriesVoiceSource> merge) =>
            new(merge.JoiningGains.Select(g => g.Voice.Voice.Name).ToList(),
                merge.LinkedGains.SelectMany(b => b.Voices).DistinctBy(v => v.Voice.Id).Select(v => v.Voice.Name).ToList());

        /// <summary>
        /// The <i>Share and link</i> confirm: the Voice names each book gains, lowest position first.
        /// Every book ends up holding every member's Voices.
        /// </summary>
        public static IReadOnlyList<SeriesBookGains> ShareAndLinkPreview(IReadOnlyList<(SeriesMemberBook Book, IReadOnlyList<SeriesVoiceCopy> Voices)> held)
        {
            var all = held.SelectMany(h => h.Voices).DistinctBy(v => v.Id).ToList();
            return held
                .Select(h =>
                {
                    var has = h.Voices.Select(v => v.Id).ToHashSet();
                    return new SeriesBookGains(h.Book.Folder, h.Book.Title, all.Where(v => !has.Contains(v.Id)).Select(v => v.Name).ToList());
                })
                .ToList();
        }
    }

    /// <summary>
    /// The registry layer of every series in the Workspace, for the Workspace-wide reads (the list,
    /// memberships, a project's series). <see cref="Unreadable"/>: how many registries could not be
    /// read and are left out.
    /// </summary>
    public sealed class SeriesWorkspaceSnapshot(IReadOnlyList<SeriesSnapshot> all, int unreadable)
    {
        public IReadOnlyList<SeriesSnapshot> All => all;
        public int Unreadable => unreadable;

        /// <summary>Every readable series, by name; each one's books in position order.</summary>
        public IReadOnlyList<SeriesView> List() =>
            all.Select(s => s.View()).OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase).ToList();

        /// <summary>Each member folder's series, for the project reads.</summary>
        public IReadOnlyDictionary<string, ProjectSeries> Memberships()
        {
            var memberships = new Dictionary<string, ProjectSeries>(StringComparer.OrdinalIgnoreCase);
            foreach (var series in List())
                foreach (var book in series.Books)
                    memberships[book.Folder] = new ProjectSeries(series.Id, series.Name, book.Position);
            return memberships;
        }

        /// <summary>The series the project is a member of, if any.</summary>
        public SeriesSnapshot? SeriesOf(Guid projectId) => all.FirstOrDefault(s => s.Members.ContainsKey(projectId));

        public ProjectSeries? MembershipOf(Guid projectId) => SeriesOf(projectId)?.MembershipOf(projectId);
    }
}
