using Read2Me.Core.Models;
using Read2Me.Services.Events;

namespace Read2Me.Services.Series
{
    /// <summary>
    /// Shared Voice edits (ADR 0018, copy after commit; #213 "Shared Voices"): a Voice of a
    /// series-linked Character is edited through the per-book routes, and once that commits its
    /// whole state is copied into every other linked book. <see cref="SeriesVoiceCopier"/> asks the
    /// snapshot who the peers are and which deletes are refused, and <see cref="SeriesVoiceReplicator"/>
    /// writes the copies; the registry forgets a linked Character once its delete commits.
    /// </summary>
    public sealed partial class SeriesRegistry
    {
        // ---- reads -------------------------------------------------------------------------------

        /// <summary>
        /// The inline series Voice editor's read (#213): the Voices of the Series copy, each linked
        /// book with its Character and its default, and the Voices whose delete is refused.
        /// </summary>
        public async Task<SeriesResult<SeriesCharacterView>> CharacterAsync(
            Guid seriesId, Guid seriesCharacterId, CancellationToken ct = default)
        {
            using var _ = await gate.EnterAsync(ct);
            var loaded = await LoadHealedAsync(seriesId, ct);
            if (loaded.Value is not { } series) return loaded.Refusal!;
            if (!series.IsSeriesCharacter(seriesCharacterId)) return SeriesRefusal.UnknownSeriesCharacter;
            return (await loader.WithVoicesAsync(series, [seriesCharacterId], ct)).Character(seriesCharacterId);
        }

        // ---- writes ------------------------------------------------------------------------------

        /// <summary>
        /// A linked Character was deleted from its book: its link goes, and the Series character too
        /// when no book is linked any more. The other books keep their Voices.
        /// </summary>
        public async Task ForgetCharacterAsync(ProjectFolderId folder, Guid characterId, CancellationToken ct = default)
        {
            using var _ = await gate.EnterAsync(ct);
            if (await loader.LoadSeriesOfAsync(folder, withCasts: false, ct) is not ({ } series, var projectId)) return;
            if (series.Peers(projectId, characterId) is null) return;
            await using (var db = await Data.Series.SeriesDbContext.OpenAsync(FolderOf(series.Id), ct))
                await RemoveLinksAsync(db, l => l.ProjectId == projectId && l.CharacterId == characterId, ct);
            Signal(series.Id, SeriesChangeKind.Changed);
        }

        /// <summary>A member book committed something the series cast board shows: its series reads again (#213, Hub).</summary>
        public async Task SignalMemberAsync(ProjectFolderId folder, CancellationToken ct = default)
        {
            if (await MembershipOfAsync(folder, ct) is { } series)
                Signal(series.SeriesId, SeriesChangeKind.Changed);
        }
    }
}
