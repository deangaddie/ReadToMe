using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Read2Me.Core.Models;
using Read2Me.Services.Events;
using Read2Me.Services.History;
using Read2Me.Services.Mutations;

namespace Read2Me.Services.Series
{
    /// <summary>
    /// The series' part in every Book commit (ADR 0018, #213 "Shared Voices"):
    /// <list type="bullet">
    /// <item>A Voice edit of a series-linked Character (<see cref="SeriesVoiceEdits"/>) is copied,
    /// once it commits, into every other linked book, in the same request. Made in the book holding
    /// the Series copy, it brings each book that differs back in line: that book takes the whole set.</item>
    /// <item>Deleting a shared Voice is refused while another book's positional rules use it, or
    /// while it is the last Voice of another linked book that holds it.</item>
    /// <item><c>MergeCharacters</c> with a linked merged Character is refused; <c>DeleteCharacter</c>
    /// of a linked one unlinks it, and its Voices go in that book only.</item>
    /// <item>A member book's commit to Characters, the narrator, Voices or Voice Rules sends the
    /// series' <c>series</c> signal.</item>
    /// </list>
    /// It plans from a <see cref="SeriesSnapshot"/> before the commit: the peers from the registry
    /// layer alone, and only for a Voice edit of a linked Character the Voices layer of its Series
    /// character. The copies are themselves Book commits, written by <see cref="SeriesVoiceReplicator"/>;
    /// while it writes, this hook stands aside for them.
    /// </summary>
    public sealed class SeriesVoiceCopier(
        IServiceProvider services, ICharacterReader characters, EventBroadcaster<SeriesChanged> changes,
        ILogger<SeriesVoiceCopier> logger) : IBookCommitHook
    {
        // Resolved when needed: the registry and the replicator write through BookMutations, which resolves this hook (#237).
        private SeriesRegistry Registry => services.GetRequiredService<SeriesRegistry>();
        private SeriesVoiceReplicator Replicator => services.GetRequiredService<SeriesVoiceReplicator>();
        private SeriesSnapshotLoader Loader => services.GetRequiredService<SeriesSnapshotLoader>();

        public async Task<BookCommitPlan> PlanAsync(BookMutation mutation, CancellationToken ct)
        {
            if (SeriesVoiceReplicator.IsWriting || SeriesVoiceEdits.IsSeriesCopy(mutation)) return BookCommitPlan.None;
            var folder = mutation.FolderId;

            switch (mutation)
            {
                case MergeCharactersMutation merge when await PeersAsync(folder, merge.MergedId, ct) is not null:
                    var name = (await characters.GetCharactersAsync(folder)).FirstOrDefault(c => c.Id == merge.MergedId)?.Name ?? "the merged Character";
                    return BookCommitPlan.Refuse(new BookMutationOutcome.Rejected(BookMutationRejection.Blocked,
                        $"{name} is linked to a series character. Unlink {name} first, or merge the other way."));

                case DeleteCharacterMutation delete when await RemembersAsync(folder, delete.CharacterId, ct):
                    return BookCommitPlan.After(_ => Registry.ForgetCharacterAsync(folder, delete.CharacterId));
            }

            if (SeriesVoiceEdits.Of(mutation) is { } edit && await CharacterOfAsync(mutation, edit) is { } characterId
                && await VoicedPeersAsync(folder, characterId, ct) is ({ } series, var peers))
            {
                if (edit is SeriesVoiceEdits.Deleted deleted
                    && await DeleteRefusalAsync(mutation, series, peers, deleted.VoiceId) is { } refusal)
                    return BookCommitPlan.Refuse(refusal);
                // Which books differed from this one is read before the edit changes it (#222): an edit
                // made in the Series copy brings them back in line. A delete removes one Voice only.
                var differing = edit is SeriesVoiceEdits.Deleted
                    ? new HashSet<string>()
                    : series.DifferingPeers(peers);
                return BookCommitPlan.After(change => CopyAsync(mutation, edit, peers, differing, change));
            }

            return BookCommitPlan.After(change =>
                (change.Receipt.Effects.Facets & SeriesVoiceEdits.SignalledFacets) != 0
                    ? SignalMemberAsync(folder)
                    : Task.CompletedTask);
        }

        // These reads take no lock (#246), so a book's commit doesn't wait behind a series write.

        /// <summary>The Character's peers from the registry layer alone: no Characters or Voices are read.</summary>
        private async Task<SeriesPeers?> PeersAsync(ProjectFolderId folder, Guid characterId, CancellationToken ct) =>
            await Loader.LoadSeriesOfAsync(folder, withCasts: false, ct) is ({ } series, var projectId)
                ? series.Peers(projectId, characterId)
                : null;

        /// <summary>Whether the series names the Character in a link or a <i>Not the same</i>, from the registry layer alone.</summary>
        private async Task<bool> RemembersAsync(ProjectFolderId folder, Guid characterId, CancellationToken ct) =>
            await Loader.LoadSeriesOfAsync(folder, withCasts: false, ct) is ({ } series, var projectId)
            && series.Remembers(projectId, characterId);

        /// <summary>A member book committed something the series cast board shows: its series reads again (#213, Hub).</summary>
        private async Task SignalMemberAsync(ProjectFolderId folder)
        {
            if (await Loader.MembershipOfAsync(folder) is { } series)
                Signal(series.SeriesId);
        }

        /// <summary>
        /// The Character's peers when it is linked and another book is linked too, with the Voices
        /// layer of its Series character only; null otherwise, and then no Voices are read.
        /// </summary>
        private async Task<(SeriesSnapshot Series, SeriesPeers Peers)?> VoicedPeersAsync(ProjectFolderId folder, Guid characterId, CancellationToken ct)
        {
            if (await Loader.LoadSeriesOfAsync(folder, withCasts: false, ct) is not ({ } series, var projectId)) return null;
            if (series.Peers(projectId, characterId) is not { Others.Count: > 0 } peers) return null;
            return (await Loader.WithVoicesAsync(series, [peers.SeriesCharacterId], ct), peers);
        }

        private async Task<Guid?> CharacterOfAsync(BookMutation mutation, SeriesVoiceEdits.Edit edit) => edit switch
        {
            SeriesVoiceEdits.Created created => created.CharacterId,
            SeriesVoiceEdits.Changed changed => (await characters.GetVoiceAsync(mutation.FolderId, changed.VoiceId))?.CharacterId,
            SeriesVoiceEdits.Deleted deleted => (await characters.GetVoiceAsync(mutation.FolderId, deleted.VoiceId))?.CharacterId,
            _ => null,
        };

        private async Task<BookMutationOutcome.Rejected?> DeleteRefusalAsync(BookMutation mutation, SeriesSnapshot series, SeriesPeers peers, Guid voiceId)
        {
            if (await characters.GetVoiceAsync(mutation.FolderId, voiceId) is not { } voice) return null;
            // A book whose Voices couldn't be read can't be checked, so the delete fails rather than
            // risk taking that book's last Voice or the Voice its rules use.
            if (peers.Others.FirstOrDefault(p => !series.VoicesRead(peers.SeriesCharacterId, p)) is { } unread)
                throw new InvalidOperationException($"The Voices of '{unread.Folder}' could not be read, so deleting a shared Voice can't be checked.");
            if (series.VoiceDeleteBlock(peers, voiceId) is not { } block) return null;
            return new BookMutationOutcome.Rejected(BookMutationRejection.Blocked, SeriesVoiceDeleteRules.Describe(voice.Name, block))
            {
                Extensions = new Dictionary<string, object?>
                {
                    ["reason"] = block.Reason.ToString(),
                    ["books"] = block.Books.Select(b => new { folder = b.Folder, title = b.Title, ruleCount = b.RuleCount }).ToList(),
                },
            };
        }

        private async Task CopyAsync(BookMutation mutation, SeriesVoiceEdits.Edit edit, SeriesPeers peers, IReadOnlySet<string> differing, CommittedBookChange change)
        {
            // A results-only commit has no step; every Voice edit is a state one, so this is a fallback.
            var label = change.StepLabel ?? ProjectHistory.LabelOf(mutation);
            var action = change.StepAction ?? StepAction.Voice;
            var voiceId = edit switch
            {
                SeriesVoiceEdits.Deleted deleted => deleted.VoiceId,
                SeriesVoiceEdits.Changed changed => changed.VoiceId,
                SeriesVoiceEdits.Created => change.Receipt.Effects.CreatedId,
                _ => null,
            };
            if (voiceId is not { } id) return;

            var plan = await PlanCopiesAsync(edit, peers, id, differing);
            await Replicator.WriteAsync(plan, new SeriesVoiceStep(label, action), CancellationToken.None);
            Signal(peers.SeriesId);
        }

        /// <summary>
        /// Each other linked book's copy, in Series position order: a delete deletes the Voice; any
        /// other edit copies the Voice's whole state, except into a book in <paramref name="differing"/>
        /// (it differed from the series copy the edit was made in), which takes the whole set instead,
        /// so the edit brings it back in line.
        /// </summary>
        private async Task<IReadOnlyList<SeriesVoiceWrite>> PlanCopiesAsync(
            SeriesVoiceEdits.Edit edit, SeriesPeers peers, Guid voiceId, IReadOnlySet<string> differing)
        {
            var others = SeriesCopies.InWriteOrder(peers.Others);
            if (edit is SeriesVoiceEdits.Deleted)
                return others.Select(b => new SeriesVoiceWrite(b, new SeriesVoiceChange.Delete(voiceId))).ToList();

            var source = peers.Self.FolderId;
            var voice = await characters.GetVoiceAsync(source, voiceId) is { } v ? SeriesVoiceSource.Of(source, v) : null;
            if (voice is null)
                logger.LogWarning("Series {SeriesId}: Voice {VoiceId} is gone from '{Folder}', where it was edited, so it was not copied", peers.SeriesId, voiceId, peers.Self.Folder);
            var set = differing.Count > 0
                ? (await characters.GetCharacterVoicesAsync(source, peers.Self.CharacterId)).Select(h => SeriesVoiceSource.Of(source, h)).ToList()
                : [];
            var plan = new List<SeriesVoiceWrite>(others.Count);
            foreach (var book in others)
            {
                if (differing.Contains(book.Folder)) plan.Add(new SeriesVoiceWrite(book, new SeriesVoiceChange.Replace(set)));
                // The Voice is gone from the book it was edited in: nothing to copy.
                else if (voice is not null) plan.Add(new SeriesVoiceWrite(book, new SeriesVoiceChange.Copy(voice)));
            }
            return plan;
        }

        private void Signal(Guid seriesId) => changes.Publish(new SeriesChanged(seriesId, SeriesChangeKind.Changed));
    }
}
