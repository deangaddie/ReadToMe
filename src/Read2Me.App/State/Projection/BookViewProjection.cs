using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Read2Me.Core.Models;
using Read2Me.Data;
using Read2Me.Data.Entities;
using Read2Me.Services;
using Read2Me.Services.Events;
using Read2Me.Services.Mutations;
using Read2Me.Services.Voice;

namespace Read2Me.App.State.Projection
{
    /// <summary>
    /// One Blazor circuit's view of one Book (ADR 0007). It binds to a project, performs the
    /// authoritative reads itself, and publishes one immutable <see cref="BookViewSnapshot"/> at a
    /// time. The persisted Book is the source of truth; this is a rebuildable projection of it.
    /// <para>
    /// A candidate is read entirely into locals and published in one assignment last. A build that
    /// fails leaves the previously published snapshot exactly as it was, so a reader never sees a
    /// half-refreshed Book View.
    /// </para>
    /// <para>
    /// Opening binds; <see cref="ApplyAsync"/> is the one way transient Book View state moves
    /// afterwards; <see cref="MutateAsync"/> is the one way the Book itself does, and it returns only
    /// once this circuit's view of the change is coherent.
    /// </para>
    /// <para>
    /// Changes committed by <em>other</em> circuits, the API, or a queue arrive as receipts on a
    /// bounded mailbox and converge this Book View on its own pump, without the writer waiting on
    /// it and without the reader navigating anywhere.
    /// </para>
    /// </summary>
    public sealed class BookViewProjection(
        IBookProjectLoader loader,
        IBookContentReader content,
        ICharacterReader characters,
        IAudioItemReader audioItems,
        BookMutations mutations,
        BookTreeState treeState,
        BookSelectionState selectionState,
        AudioItemSelectionState audioSelectionState,
        ISelectionCoordinator selections,
        IVoiceResolver voiceResolver,
        BookRevisionSequence revisions,
        ProjectDbSession session,
        EventBroadcaster<BookMutationReceipt> receipts,
        ILogger<BookViewProjection> logger) : IDisposable
    {
        /// <summary>
        /// One build at a time. Without it two overlapping opens — a fast project switch, or a
        /// re-open during a slow load — race to publish, and the one that started first can land
        /// last: a snapshot moving backwards, which is the whole failure this module exists to
        /// prevent. Serializing also means the shared state a build commits is written in the same
        /// order the snapshots are.
        /// </summary>
        private readonly SemaphoreSlim _builds = new(1, 1);

        /// <summary>
        /// This projection's identity as a mutation producer. Stamped on everything it commits, so
        /// the copy of the receipt that comes back through the broadcast can be recognised as work
        /// this circuit has already reconciled and told the reader about by simply showing it.
        /// </summary>
        private readonly Guid _originId = Guid.NewGuid();

        private readonly BookViewReceiptMailbox _mailbox = new();

        /// <summary>Stops the pump when the circuit ends.</summary>
        private readonly CancellationTokenSource _closing = new();

        /// <summary>Counts receipts taken; the pump waits on it rather than polling.</summary>
        private readonly SemaphoreSlim _arrivals = new(0);

        private bool _subscribed;

        private BookViewMode _viewMode = BookViewMode.Combined;
        private Guid? _playingAudioItemId;

        /// <summary>The latest coherent view, or null before the first successful open.</summary>
        public BookViewSnapshot? Snapshot { get; private set; }

        /// <summary>The project this projection is bound to, or null before the first successful open.</summary>
        public ProjectFolderId? Folder { get; private set; }

        /// <summary>Raised after a new snapshot is published, with the snapshot already swapped in.</summary>
        public event Action? SnapshotPublished;

        /// <summary>
        /// Raised after this Book View has converged on a change committed somewhere else — another
        /// circuit, the API, a queue — with the snapshot that change produced.
        /// <para>
        /// Whether the reader is <em>told</em> is the one rule of ADR 0007, decided here rather than
        /// by the adapter: <see cref="BookViewExternalUpdate.Announce"/> is set if and only if the
        /// reconciliation applied a structural effect or cleared a selection. Everything else —
        /// another producer's attribution, audio and review progress — converges silently, because a
        /// badge moving under a queue's work is not a surprise worth interrupting anyone for. This
        /// circuit's own mutations never raise it at all: the reader is looking at the change they
        /// just asked for.
        /// </para>
        /// </summary>
        public event Action<BookViewExternalUpdate>? ExternalUpdateApplied;

        /// <summary>
        /// Binds this projection to <paramref name="folderId"/> — switching projects if it was
        /// bound elsewhere — and publishes one coherent snapshot of that Book.
        /// <para>
        /// The binding moves only when the build succeeds. If a read fails, the projection stays
        /// bound where it was, keeps the snapshot it had, and the failure propagates.
        /// </para>
        /// </summary>
        public async Task<BookViewSnapshot> OpenAsync(ProjectFolderId folderId, CancellationToken ct = default)
        {
            await _builds.WaitAsync(ct);
            try
            {
                BindTo(folderId);
                return (await BuildAndPublishAsync(folderId, ct)).Snapshot;
            }
            finally
            {
                _builds.Release();
            }
        }

        /// <summary>
        /// Points the receipt subscription at one Book, starting it the first time. The mailbox is
        /// told rather than a field of this class set, because the answer is read on the publisher's
        /// thread: keeping the binding and the pending batch under one lock is what makes "rebound,
        /// so forget the Book we left" a single step nothing can arrive in the middle of.
        /// <para>
        /// Called from the first line of an open rather than after it succeeds, so a mutation
        /// committing while the very first build is still reading is not dropped on the floor.
        /// </para>
        /// </summary>
        private void BindTo(ProjectFolderId folderId)
        {
            _mailbox.BindTo(folderId);

            if (_subscribed) return;
            receipts.Event += OnReceipt;
            _subscribed = true;
            _ = Task.Run(() => PumpAsync(_closing.Token));
        }

        /// <summary>
        /// Takes a receipt from whichever producer committed it. This runs on that producer's commit
        /// path, so it does the least possible: hand it to the mailbox, signal the pump. It never
        /// reads, never waits and never throws — a reader must not be able to slow or fail someone
        /// else's committed mutation (ADR 0007).
        /// </summary>
        private void OnReceipt(BookMutationReceipt receipt)
        {
            try
            {
                // Already reconciled synchronously by MutateAsync, and deliberately never announced.
                if (receipt.OriginId == _originId) return;

                if (_mailbox.TryTake(receipt))
                    _arrivals.Release();
            }
            catch (Exception ex)
            {
                // Nothing here is allowed to escape onto the publisher's stack: it is standing inside
                // another producer's CommitAsync, and the worst this failure may cost is this Book
                // View's convergence, never someone else's write.
                logger.LogWarning(ex,
                    "Taking the receipt for {Mutation} on {Folder} into the Book View mailbox failed.",
                    receipt.MutationName, receipt.FolderId.Value);
            }
        }

        /// <summary>
        /// Reconciles what the mailbox holds, one batch at a time, for as long as the circuit lives.
        /// Serializing here is what makes a burst converge instead of racing: each pass takes
        /// everything that arrived while the last one was reading.
        /// </summary>
        private async Task PumpAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await _arrivals.WaitAsync(ct);
                    // Null whenever a burst was already swept up by the previous pass.
                    if (_mailbox.Drain() is { } pending)
                        await ReconcileExternalAsync(pending, ct);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    // The reader keeps the last coherent snapshot rather than a half-built one, and
                    // the pump stays alive for the next receipt. Marking the projection stale and
                    // offering recovery is the failure ticket's job.
                    logger.LogWarning(ex,
                        "Converging the Book View on {Folder} from another circuit's change failed.",
                        Folder?.Value);
                }
            }
        }

        /// <summary>
        /// Brings the Book View up to a change committed somewhere else, and decides whether it is
        /// worth mentioning.
        /// <para>
        /// Unlike the initiating path this does <em>not</em> skip a batch whose revision the
        /// published snapshot already carries. A plain build — an expansion that happened to run
        /// after the commit — stamps the new revision while doing none of a reconciliation's work: it
        /// rechecks no selection and can reach no verdict about whether to tell the reader. Treating
        /// "the content is already here" as "this batch is handled" is exactly how a selection
        /// survives the deletion of what it points at.
        /// </para>
        /// </summary>
        private async Task ReconcileExternalAsync(PendingReconciliation pending, CancellationToken ct)
        {
            await _builds.WaitAsync(ct);
            try
            {
                if (Folder is not { } folder || folder != pending.FolderId || Snapshot is null)
                    return;

                // The commit happened in another circuit, so it evicted *its* tracking context, not
                // this one. Without this, the authoritative reads below are answered out of an
                // identity map built before the write and the Book View converges on nothing.
                session.Refresh(folder);

                var built = await ReconcileToAsync(folder, pending.Effects, ct);

                // From the mailbox's own record rather than from the effects: an overflowing batch
                // degrades those to "every facet", which would announce a queue's routine progress.
                var announce = pending.Structural || built.ClearedSelection;
                ExternalUpdateApplied?.Invoke(new BookViewExternalUpdate(built.Snapshot, announce));
            }
            finally
            {
                _builds.Release();
            }
        }

        /// <summary>
        /// Rebuilds the bound Book from what a commit actually changed: expansion carried across the
        /// structural relationships first, so the reader keeps their place, then one authoritative
        /// build that rechecks both selections against the new revision.
        /// <para>
        /// Callers hold <c>_builds</c> and have already established that <paramref name="folder"/> is
        /// the bound Book.
        /// </para>
        /// </summary>
        private async Task<BuildOutcome> ReconcileToAsync(
            ProjectFolderId folder, BookMutationEffects effects, CancellationToken ct)
        {
            CarryExpansionAcross(folder, effects);
            return await BuildAndPublishAsync(folder, ct, effects);
        }

        /// <summary>
        /// The one entry for transient Book View state (ADR 0007). Every accepted gesture ends in one
        /// atomically published snapshot whose content and transient state agree, so no caller has to
        /// remember a follow-up refresh, and no two gestures can interleave into a mixed view.
        /// <para>
        /// A gesture that changes which content the view shows — expansion, and a mode switch, whose
        /// voice previews must be re-read because voice rules can have changed on another tab — is
        /// answered with a fresh build. A gesture that changes only view state is published straight
        /// onto the current snapshot: the Book has not moved, so re-reading it would cost a Book View
        /// full of reads per checkbox.
        /// </para>
        /// <para>
        /// An intent that changes nothing (expanding what is open, re-picking the current mode) is not
        /// accepted: the current snapshot comes back unchanged and unpublished.
        /// </para>
        /// </summary>
        /// <exception cref="InvalidOperationException">The projection is not open on a Book yet.</exception>
        public async Task<BookViewSnapshot> ApplyAsync(BookViewIntent intent, CancellationToken ct = default)
        {
            await _builds.WaitAsync(ct);
            try
            {
                if (Folder is not { } folder || Snapshot is not { } current)
                    throw new InvalidOperationException("A Book View intent needs a projection already open on a Book.");

                return await ApplyToAsync(folder, current, intent, ct);
            }
            finally
            {
                _builds.Release();
            }
        }

        /// <summary>
        /// Commits one Book mutation and returns only once this circuit's Book View shows it. The
        /// gesture's caller therefore never has to remember a follow-up refresh, and success never
        /// means "committed, look again in a moment" (ADR 0007).
        /// <para>
        /// Reconciliation runs under this projection's own lifetime rather than the caller's token.
        /// Past the commit point the change is real, and a cancelled reconciliation must not be able
        /// to make a committed mutation look uncommitted.
        /// </para>
        /// <para>
        /// A mutation that changes nothing publishes nothing: no revision was consumed, so there is
        /// no new Book to show. An expected refusal leaves the Book View exactly as it was.
        /// </para>
        /// </summary>
        /// <exception cref="InvalidOperationException">
        /// The projection is not open on this mutation's Book.
        /// </exception>
        public async Task<BookViewMutationOutcome> MutateAsync(
            BookMutation mutation, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(mutation);

            if (Folder != mutation.FolderId)
                throw new InvalidOperationException(
                    $"A Book mutation for '{mutation.FolderId.Value}' needs a projection open on that Book.");

            // Stamped so the broadcast copy of this receipt can be recognised as this circuit's own
            // work: it is reconciled below, synchronously, and must never come back as a surprise.
            var outcome = await mutations.CommitAsync(mutation with { OriginId = _originId }, ct);
            return outcome switch
            {
                BookMutationOutcome.Committed committed =>
                    new BookViewMutationOutcome.Coherent(
                        committed.Receipt, await ReconcileAsync(committed.Receipt)),
                BookMutationOutcome.NoChange => new BookViewMutationOutcome.NoChange(),
                BookMutationOutcome.Rejected rejected =>
                    new BookViewMutationOutcome.Uncommitted(rejected.Reason, rejected.Message),
                // Unreachable — the hierarchy is closed by a private constructor — but C# cannot prove it.
                _ => throw new NotSupportedException($"Unhandled mutation outcome {outcome.GetType().Name}."),
            };
        }

        /// <summary>
        /// Brings the published snapshot up to a committed receipt.
        /// <para>
        /// This family's effects are structural, so the answer is always an authoritative rebuild:
        /// structure moves counts and roll-up denominators that no single node's data carries.
        /// Targeted refresh for the precise item-level families arrives with their own slices.
        /// </para>
        /// </summary>
        private async Task<BookViewSnapshot> ReconcileAsync(BookMutationReceipt receipt)
        {
            // Not the caller's token: see MutateAsync.
            await _builds.WaitAsync(CancellationToken.None);
            try
            {
                if (Folder is not { } folder || folder != receipt.FolderId || Snapshot is null)
                    // The mutation committed; only this circuit's view of it is lost, because the
                    // reader switched projects underneath it. Reporting that as a stale projection
                    // rather than a throw is the recovery ticket's job.
                    throw new InvalidOperationException(
                        $"{receipt.MutationName} committed on '{receipt.FolderId.Value}', but the " +
                        "projection had already moved off that Book and cannot show it.");

                // Deliberately not skipped when the published snapshot already carries this revision.
                // A build that raced the commit — an expansion, an open — stamps the new revision
                // while rechecking no selection against it, so treating its snapshot as a
                // reconciliation is how a selection outlives the rows it points at.
                return (await ReconcileToAsync(folder, receipt.Effects, CancellationToken.None)).Snapshot;
            }
            finally
            {
                _builds.Release();
            }
        }

        /// <summary>
        /// Rebuilds the bound Book from authoritative reads and republishes it — the recovery and
        /// legacy-caller seam, for the families that still write outside <see cref="MutateAsync"/>.
        /// </summary>
        public async Task<BookViewSnapshot> RebuildAsync(CancellationToken ct = default)
        {
            await _builds.WaitAsync(ct);
            try
            {
                if (Folder is not { } folder)
                    throw new InvalidOperationException("A rebuild needs a projection already open on a Book.");

                return (await BuildAndPublishAsync(folder, ct)).Snapshot;
            }
            finally
            {
                _builds.Release();
            }
        }

        private async Task<BookViewSnapshot> ApplyToAsync(
            ProjectFolderId folder, BookViewSnapshot current, BookViewIntent intent, CancellationToken ct)
        {
            switch (intent)
            {
                case BookViewIntent.SetNodeExpanded e:
                    if (!TrySetExpanded(folder, e.Level, e.NodeId, e.Expanded)) return current;
                    return (await BuildAndPublishAsync(folder, ct)).Snapshot;

                case BookViewIntent.SetViewMode m:
                    if (m.Mode == _viewMode) return current;
                    _viewMode = m.Mode;
                    // Each mode selects a different thing — paragraphs to attribute, items to speak —
                    // so a selection made under the old one has no meaning under the new one.
                    selectionState.Reset(folder);
                    audioSelectionState.Reset(folder);
                    return (await BuildAndPublishAsync(folder, ct)).Snapshot;

                case BookViewIntent.TogglePlayback p:
                    _playingAudioItemId = _playingAudioItemId == p.ItemId ? null : p.ItemId;
                    return PublishTransient(folder, current);

                case BookViewIntent.SetParagraphSelected s:
                    await selections.ToggleParagraphAsync(
                        folder, s.ParagraphId, s.Ancestry.ChapterId, s.Ancestry.PartId, s.Ancestry.VolumeId, s.Selected);
                    return PublishTransient(folder, current);

                case BookViewIntent.SetNodeParagraphsSelected s:
                    await selections.SetNodeAsync(folder, s.Level, s.NodeId, s.Selected, s.UnattributedOnly);
                    return PublishTransient(folder, current);

                case BookViewIntent.SetBulkAssign b:
                    selectionState.For(folder).BulkMode = b.Armed;
                    return PublishTransient(folder, current);

                case BookViewIntent.SetAudioItemSelected s:
                    await selections.ToggleAudioItemAsync(s.Item, s.Selected);
                    return PublishTransient(folder, current);

                case BookViewIntent.SetNodeAudioItemsSelected s:
                    await selections.SetAudioNodeAsync(
                        folder, s.Level, s.NodeId, s.Selected, s.NeedsAudioOnly, current.NarratorOnlyMode);
                    return PublishTransient(folder, current);

                case BookViewIntent.QueueSelectedParagraphs:
                    await selections.AddSelectionToCharacterQueueAsync();
                    return PublishTransient(folder, current);

                case BookViewIntent.QueueSelectedAudioItems:
                    await selections.AddSelectionToAudioQueueAsync();
                    return PublishTransient(folder, current);

                default:
                    throw new ArgumentOutOfRangeException(nameof(intent), intent, "Unknown Book View intent.");
            }
        }

        /// <summary>
        /// Records that a node was opened or closed. Returns false when the intent already said so —
        /// there is nothing to republish for a gesture that changes nothing.
        /// <para>
        /// Closing needs no cascade: the next build walks down from the volumes that are open, so a
        /// closed branch's descendants are neither read nor kept as intent.
        /// </para>
        /// </summary>
        private bool TrySetExpanded(ProjectFolderId folder, BookNodeLevel level, Guid nodeId, bool expanded)
        {
            var open = treeState.For(folder).At(level);
            return expanded ? open.Add(nodeId) : open.Remove(nodeId);
        }

        /// <summary>
        /// Publishes transient state onto the content already on screen. Safe precisely because none
        /// of it is derived from the Book: the same revision, the same reads, one new snapshot.
        /// </summary>
        private BookViewSnapshot PublishTransient(ProjectFolderId folder, BookViewSnapshot current) =>
            Publish(current with
            {
                Selections = CurrentSelections(folder),
                ViewMode = _viewMode,
                PlayingAudioItemId = _playingAudioItemId,
            });

        /// <summary>Both selections as they stand, for the snapshot to carry read-only.</summary>
        private BookViewSelections CurrentSelections(ProjectFolderId folder)
        {
            var selection = selectionState.For(folder);
            return new BookViewSelections(
                selection.SelectedParagraphIds().ToHashSet(),
                selection.BulkMode,
                audioSelectionState.For(folder).SelectedItems().Select(i => i.ParagraphItemId).ToHashSet());
        }

        /// <summary>
        /// Keeps the reader's place across a structural change, from the relationships the receipt
        /// states: a split opens the new sibling if the source was open, and a merge moves what was
        /// open on the node that went away onto its survivor.
        /// <para>
        /// Level-agnostic on purpose. Node ids are unique across the hierarchy, so "wherever the
        /// source was open" needs no level from the writer, and a writer that had to name one would
        /// be reasoning about the view.
        /// </para>
        /// </summary>
        private void CarryExpansionAcross(ProjectFolderId folderId, BookMutationEffects effects)
        {
            var expansion = treeState.For(folderId);
            foreach (var relation in effects.Structural)
            {
                switch (relation.Kind)
                {
                    case BookStructuralRelationKind.Split:
                        expansion.CarrySplitExpansion(relation.SourceId, relation.ResultId);
                        break;
                    case BookStructuralRelationKind.Merge:
                        expansion.FixMergeExpansion(relation.ResultId, relation.SourceId);
                        break;
                }
            }
        }

        /// <summary>
        /// Both selections re-evaluated against the persisted Book, keyed by chapter because that is
        /// the unit both eligibility reads take.
        /// <para>
        /// A selection survives only while its rows are still present and still eligible, and the
        /// ancestry comes back from the read rather than from what the row was selected under — a
        /// split moves Paragraphs between Chapters and Chapters between Parts, so a preserved
        /// selection with stale ancestry would roll up under a node it is no longer in.
        /// </para>
        /// </summary>
        private readonly record struct SurvivingSelections(
            IReadOnlyList<CharacterParagraphRef> Paragraphs,
            IReadOnlyList<Guid> LostParagraphIds,
            IReadOnlyList<AudioItemRef> AudioItems,
            IReadOnlyList<Guid> LostAudioItemIds)
        {
            public bool ClearedAnything => LostParagraphIds.Count > 0 || LostAudioItemIds.Count > 0;
        }

        private async Task<SurvivingSelections> RecheckSelectionsAsync(
            ProjectFolderId folderId, BookMutationEffects effects, bool narratorOnlyMode, CancellationToken ct)
        {
            var selection = selectionState.For(folderId);
            var audioSelection = audioSelectionState.For(folderId);
            var selectedParagraphIds = selection.SelectedParagraphIds().ToHashSet();
            var selectedItems = audioSelection.SelectedItems().ToList();

            if (selectedParagraphIds.Count == 0 && selectedItems.Count == 0)
                return new SurvivingSelections([], [], [], []);

            // Where to look: the chapters the rows were selected under, plus both ends of every
            // structural relationship — a chapter split moves rows into a chapter nothing was
            // selected under yet, and a chapter merge moves them into the survivor, so looking only
            // at the chapter they were selected under would clear rows that in fact survived.
            var chapterIds = new HashSet<Guid>();
            foreach (var id in selectedParagraphIds)
                if (selection.GetAncestry(id) is { } ancestry) chapterIds.Add(ancestry.ChapterId);
            foreach (var item in selectedItems)
                chapterIds.Add(item.ChapterId);
            foreach (var relation in effects.Structural)
            {
                chapterIds.Add(relation.SourceId);
                chapterIds.Add(relation.ResultId);
            }

            var eligibleParagraphs = new Dictionary<Guid, CharacterParagraphRef>();
            var eligibleItems = new Dictionary<Guid, AudioItemRef>();
            foreach (var chapterId in chapterIds)
            {
                ct.ThrowIfCancellationRequested();

                if (selectedParagraphIds.Count > 0)
                {
                    // A node id that is not a chapter — the far end of a volume or part split —
                    // simply reads back empty, so no caller has to know which level it names.
                    foreach (var reference in await characters.GetCharacterParagraphsAsync(
                                 folderId, BookNodeLevel.Chapter, chapterId))
                        eligibleParagraphs[reference.ParagraphId] = reference;
                }

                if (selectedItems.Count > 0)
                {
                    foreach (var reference in await audioItems.GetAudioItemRefsAsync(
                                 folderId, BookNodeLevel.Chapter, chapterId,
                                 needsAudioOnly: false, narratorOnlyMode))
                        eligibleItems[reference.ParagraphItemId] = reference;
                }
            }

            return new SurvivingSelections(
                [.. selectedParagraphIds.Where(eligibleParagraphs.ContainsKey).Select(id => eligibleParagraphs[id])],
                [.. selectedParagraphIds.Where(id => !eligibleParagraphs.ContainsKey(id))],
                [.. selectedItems.Where(i => eligibleItems.ContainsKey(i.ParagraphItemId))
                    .Select(i => eligibleItems[i.ParagraphItemId])],
                [.. selectedItems.Where(i => !eligibleItems.ContainsKey(i.ParagraphItemId))
                    .Select(i => i.ParagraphItemId)]);
        }

        /// <summary>
        /// Drops what no longer holds and restamps what does, so the selections the snapshot is
        /// about to carry were computed against the same revision as its content.
        /// </summary>
        private void ApplySurvivingSelections(ProjectFolderId folderId, SurvivingSelections surviving)
        {
            var selection = selectionState.For(folderId);
            selection.RemoveParagraphs(surviving.LostParagraphIds);
            selection.AddParagraphs(surviving.Paragraphs);

            var audioSelection = audioSelectionState.For(folderId);
            audioSelection.RemoveItems(surviving.LostAudioItemIds);
            audioSelection.AddItems(surviving.AudioItems);
        }

        /// <summary>
        /// A published snapshot and the one thing about producing it that the snapshot cannot say:
        /// whether reconciling dropped a selection the reader had made. That is what decides,
        /// together with structure, whether an external change is worth announcing.
        /// </summary>
        private readonly record struct BuildOutcome(BookViewSnapshot Snapshot, bool ClearedSelection);

        /// <param name="reconciling">
        /// The effects being reconciled, when this build answers a committed mutation. Its presence
        /// is what makes the build recheck both selections against the new revision — an ordinary
        /// open or expansion has not moved the Book, so there is nothing for them to have lost.
        /// </param>
        private async Task<BuildOutcome> BuildAndPublishAsync(
            ProjectFolderId folderId, CancellationToken ct, BookMutationEffects? reconciling = null)
        {
            // Read before the reads below, never after: a mutation committing while this build is in
            // flight then carries a higher revision than the snapshot it raced, so its receipt still
            // reconciles instead of being discarded as already reflected.
            var revision = revisions.Current(folderId);

            var book = await loader.LoadSnapshotAsync(folderId, ct);
            var requested = RequestedExpansion(folderId, book.Volumes);
            var loaded = await LoadExpandedBranchesAsync(folderId, book.Volumes, requested, ct);
            var previews = await ResolveVoicePreviewsAsync(folderId, loaded.Branches, ct);
            var surviving = reconciling is { } effects
                ? await RecheckSelectionsAsync(folderId, effects, book.NarratorOnlyMode, ct)
                : (SurvivingSelections?)null;

            // Everything above is a read into locals. Only past this line does the projection — or
            // any state it shares with the rest of the circuit — actually change.
            if (Folder is { } bound && bound != folderId)
                DiscardTransientState(bound);

            Folder = folderId;
            selections.SetCurrentFolder(folderId);
            CommitExpansionIntent(folderId, loaded.Expansion);

            var selection = selectionState.For(folderId);
            var audioSelection = audioSelectionState.For(folderId);
            selection.SetCounts(book.NodeCharacterParagraphCounts);
            audioSelection.SetCounts(book.AudioNodeCounts);

            // Before CurrentSelections below, so what the snapshot carries is what survived — a
            // cleared selection and the content it no longer matches are published together, never
            // as two updates a reader could see between.
            if (surviving is { } recomputed)
                ApplySurvivingSelections(folderId, recomputed);

            var published = Publish(new BookViewSnapshot
            {
                Folder = folderId,
                Revision = revision,
                Health = BookViewHealth.Coherent,
                Filename = book.Filename,
                HasContent = book.HasContent,
                Volumes = book.Volumes,
                TotalParts = book.TotalParts,
                TotalChapters = book.TotalChapters,
                NarratorOnlyMode = book.NarratorOnlyMode,
                SelectableNodeIds = book.SelectableNodeIds,
                NodeCharacterParagraphCounts = book.NodeCharacterParagraphCounts,
                AudioNodeCounts = book.AudioNodeCounts,
                Characters = book.Characters,
                Narrator = book.Narrator ?? NarratorIdentity.Unlinked,
                Branches = loaded.Branches,
                Expansion = loaded.Expansion,
                NodeStatus = book.NodeStatusSeed,
                Reviews = book.AudioReviews.ToDictionary(r => r.ParagraphItemId, r => r.Info),
                VoicePreviews = previews,
                Selections = CurrentSelections(folderId),
                ViewMode = _viewMode,
                PlayingAudioItemId = _playingAudioItemId,
            });

            return new BuildOutcome(published, surviving?.ClearedAnything ?? false);
        }

        private BookViewSnapshot Publish(BookViewSnapshot snapshot)
        {
            Snapshot = snapshot;
            SnapshotPublished?.Invoke();
            return snapshot;
        }

        /// <summary>
        /// What the reader had open, as ids to try. A single volume is always open: with nothing to
        /// choose between, a closed one is only an extra click.
        /// </summary>
        private BookViewExpansion RequestedExpansion(ProjectFolderId folderId, IReadOnlyList<Volume> volumes)
        {
            var tree = treeState.For(folderId);
            var volumeIds = volumes.Where(v => tree.ExpandedVolumeIds.Contains(v.Id)).Select(v => v.Id).ToHashSet();

            if (volumes.Count == 1)
                volumeIds.Add(volumes[0].Id);

            return new BookViewExpansion(volumeIds, tree.ExpandedPartIds.ToHashSet(), tree.ExpandedChapterIds.ToHashSet());
        }

        /// <summary>What one branch walk loaded, and the expansion intent that survived it.</summary>
        private readonly record struct LoadedBranches(BookViewBranches Branches, BookViewExpansion Expansion);

        /// <summary>
        /// Reads only the branches the expansion intent asks for, one level at a time — a Book is
        /// never read whole, so a collapsed volume costs nothing however large it is.
        /// <para>
        /// The intent that comes back names only nodes the walk actually met, so ids left over from
        /// deleted nodes drop out rather than accumulating across rebuilds.
        /// </para>
        /// </summary>
        private async Task<LoadedBranches> LoadExpandedBranchesAsync(
            ProjectFolderId folderId,
            IReadOnlyList<Volume> volumes,
            BookViewExpansion requested,
            CancellationToken ct)
        {
            var partsByVolume = new Dictionary<Guid, IReadOnlyList<Part>>();
            var chaptersByPart = new Dictionary<Guid, IReadOnlyList<Chapter>>();
            var paragraphsByChapter = new Dictionary<Guid, IReadOnlyList<Paragraph>>();

            var openVolumes = new HashSet<Guid>();
            var openParts = new HashSet<Guid>();
            var openChapters = new HashSet<Guid>();

            foreach (var volume in volumes.Where(v => requested.VolumeIds.Contains(v.Id)))
            {
                ct.ThrowIfCancellationRequested();
                openVolumes.Add(volume.Id);
                var parts = (await content.GetChildrenAsync(folderId, BookNodeLevel.Volume, volume.Id)).Parts ?? [];
                partsByVolume[volume.Id] = parts;

                // A lone part is not a choice either: the tree hides it and renders its chapters
                // directly under the volume, so it has to be loaded for the volume to look open.
                var open = parts.Count == 1 ? parts : parts.Where(p => requested.PartIds.Contains(p.Id)).ToList();

                foreach (var part in open)
                {
                    ct.ThrowIfCancellationRequested();
                    openParts.Add(part.Id);
                    var chapters = (await content.GetChildrenAsync(folderId, BookNodeLevel.Part, part.Id)).Chapters ?? [];
                    chaptersByPart[part.Id] = chapters;

                    foreach (var chapter in chapters.Where(c => requested.ChapterIds.Contains(c.Id)))
                    {
                        ct.ThrowIfCancellationRequested();
                        openChapters.Add(chapter.Id);
                        paragraphsByChapter[chapter.Id] =
                            (await content.GetChildrenAsync(folderId, BookNodeLevel.Chapter, chapter.Id)).Paragraphs ?? [];
                    }
                }
            }

            return new LoadedBranches(
                new BookViewBranches(partsByVolume, chaptersByPart, paragraphsByChapter),
                new BookViewExpansion(openVolumes, openParts, openChapters));
        }

        /// <summary>
        /// Names the Voice each loaded item would actually be spoken in. Bounded by what is loaded,
        /// so an unexpanded Book resolves nothing, and read with the rest of the snapshot so a
        /// preview and the content it labels always come from the same revision.
        /// </summary>
        private async Task<IReadOnlyDictionary<Guid, string?>> ResolveVoicePreviewsAsync(
            ProjectFolderId folderId, BookViewBranches branches, CancellationToken ct)
        {
            var itemIds = branches.AllParagraphs().SelectMany(p => p.Items).Select(i => i.Id).ToList();
            if (itemIds.Count == 0)
                return new Dictionary<Guid, string?>();

            return await voiceResolver.ResolveNamesAsync(folderId, itemIds, ct);
        }

        private void CommitExpansionIntent(ProjectFolderId folderId, BookViewExpansion expansion)
        {
            var tree = treeState.For(folderId);
            Replace(tree.ExpandedVolumeIds, expansion.VolumeIds);
            Replace(tree.ExpandedPartIds, expansion.PartIds);
            Replace(tree.ExpandedChapterIds, expansion.ChapterIds);

            static void Replace(HashSet<Guid> target, IReadOnlySet<Guid> source)
            {
                target.Clear();
                foreach (var id in source) target.Add(id);
            }
        }

        /// <summary>
        /// Leaves nothing of the project being switched away from that could reach the new one.
        /// Selections are per-project state a roll-up could still be computed from; view mode and
        /// playback are this projection's own and start fresh on a different Book.
        /// </summary>
        private void DiscardTransientState(ProjectFolderId previous)
        {
            selectionState.Reset(previous);
            audioSelectionState.Reset(previous);
            _viewMode = BookViewMode.Combined;
            _playingAudioItemId = null;
        }

        /// <summary>
        /// Ends the circuit's interest in other producers' work: no more receipts are taken, and the
        /// pump stops. Deliberately does not wait for a reconciliation already in flight — nobody is
        /// left to see its snapshot, and the commit it answers is long since durable.
        /// </summary>
        public void Dispose()
        {
            if (_subscribed)
            {
                receipts.Event -= OnReceipt;
                _subscribed = false;
            }

            _mailbox.Close();

            // Cancelled, not disposed: the pump may be sitting on this token, and taking the source
            // out from under it would turn a tidy shutdown into an exception on a thread with nobody
            // left to report it to.
            _closing.Cancel();
        }
    }
}
