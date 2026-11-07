using System.Collections.Concurrent;
using System.Security.Cryptography;
using Read2Me.Services.Events;
using Read2Me.Services.History;

namespace Read2Me.Services.Series
{
    /// <summary>
    /// Differs from series (#222, #205 item 5): a linked book whose copy of a Series character's
    /// Voices is not the <b>Series copy</b> (<see cref="SeriesCopyChoice"/>). It is found live from
    /// the snapshot's Voices layer, by comparing each book's Voices and reference audio, so a copy
    /// that failed to write and a Restore to older Voices end in the same state with nothing stored.
    /// The user resolves it by pushing a book's Voices to every other linked book, or taking the
    /// Series copy into it.
    /// </summary>
    public sealed partial class SeriesRegistry
    {
        public const string PushLabel = "Push series voices";
        public const string TakeLabel = "Take series voices";

        // ---- writes ------------------------------------------------------------------------------

        /// <summary>
        /// Push: this book's Voices of the Series character are copied into every other linked book,
        /// in Series position order, each a "Push series voices" step there. Refused unless the book
        /// differs. A book whose copy fails is named in a 422 once the rest are written.
        /// </summary>
        public Task<SeriesResult<bool>> PushAsync(Guid seriesId, Guid seriesCharacterId, string folder, CancellationToken ct = default) =>
            ResolveAsync(seriesId, seriesCharacterId, folder, push: true, ct);

        /// <summary>
        /// Take: the Series copy's Voices are copied into this book, a "Take series voices" step
        /// there. Refused unless the book differs.
        /// </summary>
        public Task<SeriesResult<bool>> TakeAsync(Guid seriesId, Guid seriesCharacterId, string folder, CancellationToken ct = default) =>
            ResolveAsync(seriesId, seriesCharacterId, folder, push: false, ct);

        /// <summary>
        /// What a push from, or take into, <paramref name="folder"/> would remove, for its confirm: each
        /// target book that loses Voices, with the positional rules on them. Refused as the write would be.
        /// </summary>
        public async Task<SeriesResult<SeriesResolvePreview>> PreviewResolveAsync(
            Guid seriesId, Guid seriesCharacterId, string folder, bool push, CancellationToken ct = default)
        {
            using var _ = await gate.EnterAsync(ct);
            var planned = await PlanResolveAsync(seriesId, seriesCharacterId, folder, push, ct);
            if (planned.Value is not ({ } series, { } plan)) return planned.Refusal!;
            return new SeriesResolvePreview(series.Losses(seriesCharacterId, plan.Source, plan.Targets));
        }

        private sealed record PlannedResolve(SeriesSnapshot Series, SeriesResolvePlan Plan);

        /// <summary>The series with the Voices layer of the Series character, and the push or take planned from it.</summary>
        private async Task<SeriesResult<PlannedResolve>> PlanResolveAsync(
            Guid seriesId, Guid seriesCharacterId, string folder, bool push, CancellationToken ct)
        {
            var loaded = await LoadHealedAsync(seriesId, ct);
            if (loaded.Value is not { } registryLayer) return loaded.Refusal!;
            var series = await loader.WithVoicesAsync(registryLayer, [seriesCharacterId], ct);
            var planned = series.PlanResolve(seriesCharacterId, folder, push);
            if (planned.Value is not { } plan) return planned.Refusal!;
            return new PlannedResolve(series, plan);
        }

        private async Task<SeriesResult<bool>> ResolveAsync(
            Guid seriesId, Guid seriesCharacterId, string folder, bool push, CancellationToken ct)
        {
            using var hold = await gate.EnterAsync(ct);
            var planned = await PlanResolveAsync(seriesId, seriesCharacterId, folder, push, ct);
            if (planned.Value is not ({ } series, { } plan)) return planned.Refusal!;

            // The source's whole set: each target holds exactly these afterwards.
            var source = series.VoicesOf(seriesCharacterId, plan.Source);
            var writes = SeriesCopies.InWriteOrder(plan.Targets)
                .Select(t => new SeriesVoiceWrite(t, new SeriesVoiceChange.Replace(source)))
                .ToList();
            var results = await replicator.WriteAsync(writes, new SeriesVoiceStep(plan.Step, StepAction.Voice), hold, CancellationToken.None);
            Signal(seriesId, SeriesChangeKind.Changed);

            var failures = results.Where(r => r.Failure is not null)
                .Select(r => (Title: plan.Linked.First(p => p.Folder == r.Folder).Title, Reason: r.Failure!))
                .ToList();
            return failures.Count > 0 ? SeriesRefusal.CopiesFailed(failures) : true;
        }
    }

    /// <summary>A push or take's preview: each target book that would lose Voices, and the positional rules on them.</summary>
    public sealed record SeriesResolvePreview(IReadOnlyList<SeriesVoiceLoss> Losses);

    /// <summary>
    /// Reference audio content hashes for the <i>differs</i> check, kept per file by its size and
    /// write time so a board read doesn't hash every Voice of every book again (process-wide).
    /// </summary>
    public sealed class VoiceAudioHashes
    {
        private readonly ConcurrentDictionary<string, (long Length, long Ticks, string Hash)> _known =
            new(StringComparer.OrdinalIgnoreCase);

        public async Task<string> HashAsync(string path)
        {
            var info = new FileInfo(path);
            var (length, ticks) = (info.Length, info.LastWriteTimeUtc.Ticks);
            if (_known.TryGetValue(path, out var known) && known.Length == length && known.Ticks == ticks)
                return known.Hash;

            await using var stream = File.OpenRead(path);
            var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream));
            _known[path] = (length, ticks, hash);
            return hash;
        }
    }
}
