using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Read2Me.Core.Models;
using Read2Me.Data;
using Read2Me.Data.Entities;
using Read2Me.Services;
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
    /// afterwards. Committing mutations and reconciling from receipts arrive with the tickets that
    /// follow.
    /// </para>
    /// </summary>
    public sealed class BookViewProjection(
        IBookProjectLoader loader,
        IBookContentReader content,
        BookTreeState treeState,
        BookSelectionState selectionState,
        AudioItemSelectionState audioSelectionState,
        ISelectionCoordinator selections,
        IVoiceResolver voiceResolver,
        BookRevisionSequence revisions)
    {
        /// <summary>
        /// One build at a time. Without it two overlapping opens — a fast project switch, or a
        /// re-open during a slow load — race to publish, and the one that started first can land
        /// last: a snapshot moving backwards, which is the whole failure this module exists to
        /// prevent. Serializing also means the shared state a build commits is written in the same
        /// order the snapshots are.
        /// </summary>
        private readonly SemaphoreSlim _builds = new(1, 1);

        private BookViewMode _viewMode = BookViewMode.Combined;
        private Guid? _playingAudioItemId;

        /// <summary>The latest coherent view, or null before the first successful open.</summary>
        public BookViewSnapshot? Snapshot { get; private set; }

        /// <summary>The project this projection is bound to, or null before the first successful open.</summary>
        public ProjectFolderId? Folder { get; private set; }

        /// <summary>Raised after a new snapshot is published, with the snapshot already swapped in.</summary>
        public event Action? SnapshotPublished;

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
                return await BuildAndPublishAsync(folderId, ct);
            }
            finally
            {
                _builds.Release();
            }
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
        /// Rebuilds the bound Book from authoritative reads and republishes it.
        /// <para>
        /// The interim seam for callers that have just written to the Book: from ticket 04 on, those
        /// writes arrive as receipts and reconcile themselves, and this stops being called by hand.
        /// </para>
        /// </summary>
        public async Task<BookViewSnapshot> RebuildAsync(CancellationToken ct = default)
        {
            await _builds.WaitAsync(ct);
            try
            {
                if (Folder is not { } folder)
                    throw new InvalidOperationException("A rebuild needs a projection already open on a Book.");

                return await BuildAndPublishAsync(folder, ct);
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
                    return await BuildAndPublishAsync(folder, ct);

                case BookViewIntent.SetViewMode m:
                    if (m.Mode == _viewMode) return current;
                    _viewMode = m.Mode;
                    // Each mode selects a different thing — paragraphs to attribute, items to speak —
                    // so a selection made under the old one has no meaning under the new one.
                    selectionState.Reset(folder);
                    audioSelectionState.Reset(folder);
                    return await BuildAndPublishAsync(folder, ct);

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

        private async Task<BookViewSnapshot> BuildAndPublishAsync(ProjectFolderId folderId, CancellationToken ct)
        {
            // Read before the reads below, never after: a mutation committing while this build is in
            // flight then carries a higher revision than the snapshot it raced, so its receipt still
            // reconciles instead of being discarded as already reflected.
            var revision = revisions.Current(folderId);

            var book = await loader.LoadSnapshotAsync(folderId, ct);
            var requested = RequestedExpansion(folderId, book.Volumes);
            var loaded = await LoadExpandedBranchesAsync(folderId, book.Volumes, requested, ct);
            var previews = await ResolveVoicePreviewsAsync(folderId, loaded.Branches, ct);

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

            return Publish(new BookViewSnapshot
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
    }
}
