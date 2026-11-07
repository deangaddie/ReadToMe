using Microsoft.EntityFrameworkCore;
using Read2Me.Data.Series;
using Read2Me.Services.Events;
using static Read2Me.Services.Series.SeriesNameMatch;

namespace Read2Me.Services.Series
{
    /// <summary>
    /// Link suggestions (#212): computed live by <see cref="SeriesNameMatch"/> each time the board is
    /// read (<see cref="SeriesSnapshot.Suggestions"/>), never stored. Only <i>Not the same</i> is
    /// kept, in <c>series.db</c>, until its Character is linked or deleted. <i>Share and link</i>
    /// takes a suggested row in one go.
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
            var loaded = await LoadHealedAsync(seriesId, ct);
            if (loaded.Value is not { } series) return loaded.Refusal!;
            var picked = series.UnlinkedMembers(members);
            if (picked.Value is not { } chosen) return picked.Refusal!;

            var held = new List<(SeriesMemberBook Book, IReadOnlyList<Mutations.SeriesVoiceCopy> Voices)>();
            foreach (var (book, character) in chosen)
                held.Add((book, (await VoicesAsync(book.FolderId, character.CharacterId)).Select(v => v.Voice).ToList()));
            return SeriesSnapshot.ShareAndLinkPreview(held).ToList();
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
            using var hold = await gate.EnterAsync(ct);
            var loaded = await LoadHealedAsync(seriesId, ct);
            if (loaded.Value is not { } series) return loaded.Refusal!;
            var picked = series.UnlinkedMembers(members);
            if (picked.Value is not { } chosen) return picked.Refusal!;

            var id = await ShareUncheckedAsync(seriesId, chosen[0].Book, chosen[0].Character.CharacterId, ct);
            try
            {
                foreach (var (book, character) in chosen.Skip(1))
                {
                    // Each link plans against the links and Voices left by the ones before it.
                    var reloaded = await loader.LoadSeriesAsync(seriesId, withCasts: true, ct);
                    if (reloaded.Value is not { } now) return reloaded.Refusal!;
                    var (plan, refused) = await PlanLinkAsync(now, id, book.Folder, character.CharacterId);
                    if (plan is null) return refused!;
                    if (await RunLinkAsync(seriesId, id, character.CharacterId, plan, hold, ct) is { } failed) return failed;
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
            var loaded = await LoadHealedAsync(seriesId, ct);
            if (loaded.Value is not { } series) return loaded.Refusal!;
            if (series.MemberOf(folder) is null) return series.NotAMember(folder);
            if (!series.IsSeriesCharacter(seriesCharacterId)) return SeriesRefusal.UnknownSeriesCharacter;
            var checkedBook = series.CheckUnlinked(folder, characterId);
            if (checkedBook.Value is not { } book) return checkedBook.Refusal!;

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
            var loaded = await LoadHealedAsync(seriesId, ct);
            if (loaded.Value is not { } series) return loaded.Refusal!;
            var checkedMembers = series.UnlinkedMembers(members);
            if (checkedMembers.Value is not { } picked) return checkedMembers.Refusal!;

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
    }

    /// <summary>The Voice names one book gains from <i>Share and link</i>.</summary>
    public sealed record SeriesBookGains(string Folder, string Title, IReadOnlyList<string> Gains);
}
