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
        /// <summary>
        /// A Character the series remembers (linked, or named by a <i>Not the same</i>) was deleted
        /// from its book: its link goes, and the Series character too when no book is linked any
        /// more, and so does every <i>Not the same</i> naming it. A write, so the series' other stale
        /// rows go first. The other books keep their Voices.
        /// </summary>
        public async Task ForgetCharacterAsync(ProjectFolderId folder, Guid characterId, CancellationToken ct = default)
        {
            using var _ = await gate.EnterAsync(ct);
            if (await loader.LoadSeriesOfAsync(folder, withCasts: true, ct) is not ({ } series, var projectId)) return;
            if (!series.Remembers(projectId, characterId)) return;
            await HealAsync(series, ct);
            await using (var db = await Data.Series.SeriesDbContext.OpenAsync(FolderOf(series.Id), ct))
            {
                ForgetNotTheSame(db, [new(projectId, characterId)]);
                await RemoveLinksAsync(db, l => l.ProjectId == projectId && l.CharacterId == characterId, ct);
            }
            Signal(series.Id, SeriesChangeKind.Changed);
        }
    }
}
