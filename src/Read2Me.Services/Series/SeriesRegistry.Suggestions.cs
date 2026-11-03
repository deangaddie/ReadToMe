using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Read2Me.Data.Series;
using Read2Me.Services.Events;
using static Read2Me.Services.Series.SeriesNameMatch;

namespace Read2Me.Services.Series
{
    /// <summary>
    /// Link suggestions (#212): computed live by <see cref="SeriesNameMatch"/> each time the board is
    /// read, never stored. Only <i>Not the same</i> is kept, in <c>series.db</c>, until its Character
    /// is linked or deleted. <i>Share and link</i> takes a suggested row in one go.
    /// </summary>
    public sealed partial class SeriesRegistry
    {
        /// <summary>One book's Character, named by folder, as the suggested-row routes take them.</summary>
        public readonly record struct SeriesMemberRef(string Folder, Guid CharacterId);

        // ---- reads -------------------------------------------------------------------------------

        /// <summary>
        /// What <i>Share and link</i> would copy, for its confirm: the names of the Voices each book
        /// gains, lowest position first. Every book ends up holding every member's Voices.
        /// </summary>
        public async Task<SeriesResult<IReadOnlyList<SeriesBookGains>>> PreviewShareAndLinkAsync(
            Guid seriesId, IReadOnlyList<SeriesMemberRef> members, CancellationToken ct = default)
        {
            using var _ = await gate.EnterAsync(ct);
            var (state, refusal) = await LinkStateAsync(seriesId, ct);
            if (state is null) return refusal!;
            var (picked, invalid) = await UnlinkedMembersAsync(state, members);
            if (picked is null) return invalid!;

            var held = new List<(LinkBook Book, IReadOnlyList<SourcedVoice> Voices)>();
            foreach (var (book, character) in picked)
                held.Add((book, await VoicesAsync(book, character.CharacterId)));
            var all = held.SelectMany(h => h.Voices).DistinctBy(v => v.Voice.Id).ToList();
            return held
                .Select(h =>
                {
                    var has = h.Voices.Select(v => v.Voice.Id).ToHashSet();
                    return new SeriesBookGains(h.Book.Folder, h.Book.Title, all.Where(v => !has.Contains(v.Voice.Id)).Select(v => v.Voice.Name).ToList());
                })
                .ToList();
        }

        // ---- writes ------------------------------------------------------------------------------

        /// <summary>
        /// <i>Share and link</i> on a suggested row: a Series character made from the lowest-positioned
        /// book's Character, then each other one linked to it in position order, each link running
        /// the Voice merge. A link refused part-way leaves the ones before it standing.
        /// </summary>
        public async Task<SeriesResult<Guid>> ShareAndLinkAsync(
            Guid seriesId, IReadOnlyList<SeriesMemberRef> members, CancellationToken ct = default)
        {
            using var _ = await gate.EnterAsync(ct);
            var (state, refusal) = await LinkStateAsync(seriesId, ct);
            if (state is null) return refusal!;
            var (picked, invalid) = await UnlinkedMembersAsync(state, members);
            if (picked is null) return invalid!;

            var id = await ShareUncheckedAsync(seriesId, picked[0].Book, picked[0].Character.CharacterId, ct);
            try
            {
                foreach (var (book, character) in picked.Skip(1))
                {
                    var (plan, refused) = await PlanLinkAsync(seriesId, id, book.Folder, character.CharacterId, ct);
                    if (plan is null) return refused!;
                    if (await RunLinkAsync(seriesId, id, character.CharacterId, plan, ct) is { } failed) return failed;
                }
            }
            finally
            {
                Signal(seriesId, SeriesChangeKind.Changed);
            }
            return id;
        }

        /// <summary><i>Not the same</i> on a suggested cell: that Character is not that Series character.</summary>
        public async Task<SeriesResult<bool>> NotTheSameAsync(
            Guid seriesId, Guid seriesCharacterId, string folder, Guid characterId, CancellationToken ct = default)
        {
            using var _ = await gate.EnterAsync(ct);
            var (state, refusal) = await LinkStateAsync(seriesId, ct);
            if (state is null) return refusal!;
            if (MemberOf(state, folder) is not { } book) return await NotAMemberAsync(state, folder);
            if (state.Links.All(l => l.SeriesCharacterId != seriesCharacterId)) return SeriesRefusal.UnknownSeriesCharacter;
            if (state.Find(book.ProjectId, characterId) is not { } character) return SeriesRefusal.UnknownCharacter(book.Title);
            if (SeriesLinkRules.CheckShare(state.Links, book.ProjectId, characterId) is { } linked)
                return SeriesRefusal.CharacterAlreadyLinked(character.Name, book.Title, state.LabelOf(linked.SeriesCharacterId));

            await using (var db = await SeriesDbContext.OpenAsync(FolderOf(seriesId), ct))
            {
                if (!await db.NotTheSame.AnyAsync(n => n.SeriesCharacterId == seriesCharacterId
                                                       && n.ProjectId == book.ProjectId && n.CharacterId == characterId, ct))
                {
                    db.NotTheSame.Add(new NotTheSameRow
                    {
                        Id = Guid.NewGuid(), SeriesCharacterId = seriesCharacterId, ProjectId = book.ProjectId, CharacterId = characterId,
                    });
                    await db.SaveChangesAsync(ct);
                }
            }
            Signal(seriesId, SeriesChangeKind.Changed);
            return true;
        }

        /// <summary><i>Not the same</i> on a suggested row: each pair of its Characters is recorded as not the same.</summary>
        public async Task<SeriesResult<bool>> NotTheSameAsync(
            Guid seriesId, IReadOnlyList<SeriesMemberRef> members, CancellationToken ct = default)
        {
            using var _ = await gate.EnterAsync(ct);
            var (state, refusal) = await LinkStateAsync(seriesId, ct);
            if (state is null) return refusal!;
            var (picked, invalid) = await UnlinkedMembersAsync(state, members);
            if (picked is null) return invalid!;

            await using (var db = await SeriesDbContext.OpenAsync(FolderOf(seriesId), ct))
            {
                var kept = await db.NotTheSame.Where(n => n.OtherProjectId != null).ToListAsync(ct);
                for (var i = 0; i < picked.Count; i++)
                    for (var j = i + 1; j < picked.Count; j++)
                    {
                        Member one = new(picked[i].Book.ProjectId, picked[i].Character.CharacterId);
                        Member other = new(picked[j].Book.ProjectId, picked[j].Character.CharacterId);
                        if (kept.Select(PairOf).Any(p => p.Is(one, other))) continue;
                        db.NotTheSame.Add(new NotTheSameRow
                        {
                            Id = Guid.NewGuid(),
                            ProjectId = one.ProjectId, CharacterId = one.CharacterId,
                            OtherProjectId = other.ProjectId, OtherCharacterId = other.CharacterId,
                        });
                    }
                await db.SaveChangesAsync(ct);
            }
            Signal(seriesId, SeriesChangeKind.Changed);
            return true;
        }

        // ---- internals ---------------------------------------------------------------------------

        /// <summary>The board's suggestions from the current names and aliases, each book's narrator link, and the kept <i>Not the same</i>.</summary>
        private async Task<Suggestions> SuggestAsync(LinkState state, CancellationToken ct)
        {
            var books = new List<Book>();
            foreach (var book in state.Books)
            {
                // A book whose Characters couldn't be read offers nothing, and takes no suggestion.
                if (!state.Characters.TryGetValue(book.ProjectId, out var cast)) continue;
                bool narratorLinked;
                try
                {
                    narratorLinked = (await projects.GetNarratorAsync(book.FolderId, ct)).IsLinked;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Series {SeriesId}: the narrator link of '{Folder}' could not be read", state.Series.Id, book.Folder);
                    continue;
                }
                books.Add(new Book(book.ProjectId, narratorLinked, cast
                    .Select(c => new Character(
                        c.CharacterId, c.Name, c.Aliases, c.IsNarrator,
                        state.Links.FirstOrDefault(l => l.ProjectId == book.ProjectId && l.CharacterId == c.CharacterId)?.SeriesCharacterId))
                    .ToList()));
            }

            List<NotTheSameRow> kept;
            await using (var db = await SeriesDbContext.OpenAsync(FolderOf(state.Series.Id), ct))
                kept = await db.NotTheSame.AsNoTracking().ToListAsync(ct);
            var notTheSame = new NotTheSame(
                kept.Where(n => n.SeriesCharacterId is not null)
                    .Select(n => new DismissedCell(n.SeriesCharacterId!.Value, n.ProjectId, n.CharacterId))
                    .ToList(),
                kept.Where(n => n.OtherProjectId is not null && n.OtherCharacterId is not null).Select(PairOf).ToList());
            return Suggest(books, notTheSame);
        }

        private static DismissedPair PairOf(NotTheSameRow n) =>
            new(new Member(n.ProjectId, n.CharacterId), new Member(n.OtherProjectId!.Value, n.OtherCharacterId!.Value));

        /// <summary>Drops every <i>Not the same</i> naming one of these Characters, which are being linked.</summary>
        private static void ForgetNotTheSame(SeriesDbContext db, IReadOnlyList<Member> linking)
        {
            foreach (var m in linking)
                db.NotTheSame.RemoveRange(db.NotTheSame.Where(n =>
                    (n.ProjectId == m.ProjectId && n.CharacterId == m.CharacterId)
                    || (n.OtherProjectId == m.ProjectId && n.OtherCharacterId == m.CharacterId)));
        }

        /// <summary>
        /// A suggested row's members, checked and in position order: two or more, one per book, each
        /// an unlinked Character of a member book.
        /// </summary>
        private async Task<(List<(LinkBook Book, SeriesCastCharacter Character)>? Members, SeriesRefusal? Refusal)> UnlinkedMembersAsync(
            LinkState state, IReadOnlyList<SeriesMemberRef> members)
        {
            if (members.Count < 2) return (null, SeriesRefusal.TooFewMembers);
            var picked = new List<(LinkBook Book, SeriesCastCharacter Character)>();
            foreach (var member in members)
            {
                if (MemberOf(state, member.Folder) is not { } book) return (null, await NotAMemberAsync(state, member.Folder));
                if (picked.Any(p => p.Book == book)) return (null, SeriesRefusal.BookTwice(book.Title));
                if (state.Find(book.ProjectId, member.CharacterId) is not { } character)
                    return (null, SeriesRefusal.UnknownCharacter(book.Title));
                if (SeriesLinkRules.CheckShare(state.Links, book.ProjectId, member.CharacterId) is { } linked)
                    return (null, SeriesRefusal.CharacterAlreadyLinked(character.Name, book.Title, state.LabelOf(linked.SeriesCharacterId)));
                picked.Add((book, character));
            }
            return (picked.OrderBy(p => state.Books.ToList().IndexOf(p.Book)).ToList(), null);
        }
    }

    /// <summary>The Voice names one book gains from <i>Share and link</i>.</summary>
    public sealed record SeriesBookGains(string Folder, string Title, IReadOnlyList<string> Gains);
}
