using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using Read2Me.Services.Audio;
using Read2Me.Services.Events;
using Read2Me.Services.History;
using Read2Me.Services.Mutations;
using VoiceEntity = Read2Me.Data.Entities.Voice;

namespace Read2Me.Services.Series
{
    /// <summary>
    /// Differs from series (#222, #205 item 5): a linked book whose copy of a Series character's
    /// Voices is not the <b>series copy</b> (<see cref="SeriesCopyChoice"/>). It is found live, by
    /// comparing each book's Voices and reference audio, so a copy that failed to write and a
    /// Restore to older Voices end in the same state with nothing stored. The user resolves it by
    /// pushing a book's Voices to every other linked book, or taking the series copy into it.
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
        /// Take: the series copy's Voices are copied into this book, a "Take series voices" step
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
            var loaded = await LoadHealedAsync(seriesId, ct);
            if (loaded.Value is not { } series) return loaded.Refusal!;
            var planned = await PlanResolveAsync(series, seriesCharacterId, folder, push);
            if (planned.Value is not { } plan) return planned.Refusal!;
            return new SeriesResolvePreview(await LossesAsync(plan.Source, plan.Targets));
        }

        private async Task<SeriesResult<SeriesResolvePlan>> PlanResolveAsync(
            SeriesSnapshot series, Guid seriesCharacterId, string folder, bool push) =>
            series.PlanResolve(seriesCharacterId, folder, push, await ChooseAsync(series.Id, series.LinkedPeers(seriesCharacterId)));

        private async Task<SeriesResult<bool>> ResolveAsync(
            Guid seriesId, Guid seriesCharacterId, string folder, bool push, CancellationToken ct)
        {
            using var hold = await gate.EnterAsync(ct);
            var loaded = await LoadHealedAsync(seriesId, ct);
            if (loaded.Value is not { } series) return loaded.Refusal!;
            var planned = await PlanResolveAsync(series, seriesCharacterId, folder, push);
            if (planned.Value is not { } plan) return planned.Refusal!;

            // The source's whole set: each target holds exactly these afterwards.
            var source = await VoicesAsync(plan.Source.FolderId, plan.Source.CharacterId);
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

        // ---- reads -------------------------------------------------------------------------------

        /// <summary>
        /// The other linked books an edit made in <see cref="SeriesPeers.Self"/> should bring back in
        /// line (#208, 5): when that book holds the series copy, every book that differs from it and
        /// would lose no positional rules; else none, since a differing book's edit resolves nothing silently.
        /// </summary>
        public async Task<IReadOnlySet<string>> DifferingPeersAsync(SeriesPeers peers)
        {
            using var _ = await gate.EnterAsync(CancellationToken.None);
            var choice = await ChooseAsync(peers.SeriesId, [peers.Self, .. peers.Others]);
            if (choice is null || !string.Equals(choice.SeriesCopyFolder, peers.Self.Folder, StringComparison.OrdinalIgnoreCase))
                return new HashSet<string>();

            // Nothing asks first here, so a book that would lose positional rules is left differing for a push or take.
            var differing = peers.Others.Where(p => choice.DiffersFrom(p.Folder)).ToList();
            var losing = (await LossesAsync(peers.Self, differing)).Where(l => l.RuleCount > 0).Select(l => l.Folder).ToHashSet();
            return differing.Select(p => p.Folder).Where(f => !losing.Contains(f)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        // ---- internals ---------------------------------------------------------------------------

        /// <summary>A linked book's Voices of the Series character, and their version.</summary>
        private sealed record HeldVoices(SeriesPeer Book, IReadOnlyList<VoiceEntity> Voices, string Version);

        /// <summary>Each book's Voices; a book whose Voices can't be read is logged and left out.</summary>
        private async Task<IReadOnlyList<HeldVoices>> HeldAsync(Guid seriesId, IEnumerable<SeriesPeer> books)
        {
            var held = new List<HeldVoices>();
            foreach (var book in books)
            {
                try
                {
                    var voices = await characters.GetCharacterVoicesAsync(book.FolderId, book.CharacterId);
                    var folder = fs.GetProjectFolderPath(book.FolderId);
                    var states = new List<SeriesVoiceState>(voices.Count);
                    foreach (var v in voices)
                        states.Add(new SeriesVoiceState(v.Id, v.Name, v.Description, v.Source, v.DesignPrompt, v.Transcript,
                            v.VoiceDesignSettingsOverrideJson, v.TtsSettingsOverrideJson, await AudioHashAsync(folder, v.AudioFileName)));
                    held.Add(new HeldVoices(book, voices, SeriesVoiceVersion.Of(states)));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Series {SeriesId}: the Voices of '{Folder}' could not be read", seriesId, book.Folder);
                }
            }
            return held;
        }

        /// <summary>What replacing each target's Voices with <paramref name="source"/>'s set removes; a book losing nothing is left out.</summary>
        private async Task<IReadOnlyList<SeriesVoiceLoss>> LossesAsync(SeriesPeer source, IEnumerable<SeriesPeer> targets)
        {
            var kept = (await characters.GetCharacterVoicesAsync(source.FolderId, source.CharacterId)).Select(v => v.Id).ToHashSet();
            var losses = new List<SeriesVoiceLoss>();
            foreach (var target in targets)
            {
                var held = (await characters.GetCharacterVoicesAsync(target.FolderId, target.CharacterId)).Select(v => (v.Id, v.Name)).ToList();
                var rules = (await characters.GetCharacterVoiceRulesAsync(target.FolderId, target.CharacterId))
                    .Where(r => !r.IsDefault).Select(r => r.VoiceId).ToList();
                if (SeriesVoiceLosses.Of(target.Folder, target.Title, kept, held, rules) is { } loss) losses.Add(loss);
            }
            return losses;
        }

        private static SeriesCopyChoice.Choice? Choose(IReadOnlyList<HeldVoices> held) =>
            held.Count == 0
                ? null
                : SeriesCopyChoice.Choose(held.Select(h => new SeriesCopyChoice.Book(h.Book.Folder, h.Book.Position, h.Version)).ToList());

        private async Task<SeriesCopyChoice.Choice?> ChooseAsync(Guid seriesId, IEnumerable<SeriesPeer> books) =>
            Choose(await HeldAsync(seriesId, books));

        /// <summary>The reference audio's content hash; a named file that is missing hashes as such.</summary>
        private async Task<string?> AudioHashAsync(string projectFolder, string? audioFileName)
        {
            if (audioFileName is not { Length: > 0 }) return null;
            var path = Path.Combine(projectFolder, audioFileName);
            return File.Exists(path) ? await audioHashes.HashAsync(path) : "missing";
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
