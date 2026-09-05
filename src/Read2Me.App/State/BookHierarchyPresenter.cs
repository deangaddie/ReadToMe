using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MudBlazor;
using Read2Me.App.Shared;
using Read2Me.App.State.Projection;
using Read2Me.Core.Models;
using Read2Me.Data;
using Read2Me.Data.Entities;
using Read2Me.Services;
using Read2Me.Services.Audio;
using Read2Me.Services.Characters;
using Read2Me.Services.Events;
using Read2Me.Services.Mutations;
using Read2Me.Services.NodeStatus;
using Read2Me.Services.UseCases;

namespace Read2Me.App.State
{
    /// <summary>
    /// The MudBlazor Book View's adapter over <see cref="BookViewProjection"/> (ADR 0007). Everything
    /// the tree renders is read straight off the latest published snapshot, and every transient
    /// gesture is forwarded as a <see cref="BookViewIntent"/> — so the page owns no second copy of
    /// Book View state and no rules of its own about how that state moves.
    /// <para>
    /// What is still the adapter's own: the busy and error flags of a running command, the seeding of
    /// the two singletons that mix persisted counts with live queue progress, and the Book mutations
    /// that the families still on the legacy command path own until their own slice lands.
    /// </para>
    /// </summary>
    public class BookHierarchyPresenter(
        IProjectReader reader,
        BookViewProjection projection,
        CharacterResolver characterRoster,
        BookUseCases bookUseCases,
        BookSelectionState selectionState,
        AudioItemSelectionState audioSelectionState,
        IDialogService dialogService,
        ISnackbar snackbar,
        CharacterQueueService characterQueue,
        AudioReviewService audioReviews,
        NodeStatusService nodeStatus) : IDisposable
    {
        public bool IsLoading { get; private set; }
        public bool IsBusy { get; private set; }
        public bool ConfirmReread { get; private set; }
        public string? Error { get; private set; }

        public event Action? StateChanged;

        // ---------------------------------------------------------------
        // The published snapshot, as the tree reads it
        // ---------------------------------------------------------------

        private BookViewSnapshot? Snapshot => projection.Snapshot;

        public bool HasContent => Snapshot?.HasContent ?? false;
        public string? Filename => Snapshot?.Filename;
        public IReadOnlyList<Volume> Volumes => Snapshot?.Volumes ?? [];
        public IReadOnlyList<Character> Characters => Snapshot?.Characters ?? [];
        public int TotalParts => Snapshot?.TotalParts ?? 0;
        public int TotalChapters => Snapshot?.TotalChapters ?? 0;
        public bool NarratorOnlyMode => Snapshot?.NarratorOnlyMode ?? false;

        /// <summary>
        /// Who narrates this book — read-time projection of the narrator link (ADR-0004). The
        /// character picker names it on the pinned narrator entry so "Narrator (Alice)" is visibly
        /// a different choice from "Alice".
        /// </summary>
        public NarratorIdentity Narrator => Snapshot?.Narrator ?? NarratorIdentity.Unlinked;

        /// <summary>
        /// Whether the Book View is a Stale Book View projection (ADR 0007) — still showing the last
        /// coherent snapshot, but no longer able to vouch for it. The page turns this into a banner
        /// with the retry that clears it.
        /// </summary>
        public bool IsStale => Snapshot?.Health == BookViewHealth.Stale;

        /// <summary>
        /// Whether a Book-level gesture is worth offering. The projection refuses every mutation
        /// while stale anyway; this greys out the deliberate ones — the Book menu, the node menus —
        /// so the reader is not invited to reach for them. The incidental ones, a speaker chip
        /// above all, stay clickable and answer with the refusal, because greying out every chip in
        /// the tree would read as the Book itself being broken rather than the view of it.
        /// </summary>
        public bool CanMutate => !IsBusy && !IsStale;

        public BookViewMode ViewMode => Snapshot?.ViewMode ?? BookViewMode.Combined;
        public bool SplitView => ViewMode != BookViewMode.Combined;
        public Guid? PlayingAudioItemId => Snapshot?.PlayingAudioItemId;

        /// <summary>The Voice the Audio Queue would use for an item, resolved with the snapshot.</summary>
        public string? ResolvedVoiceName(Guid itemId) => Snapshot?.ResolvedVoiceName(itemId);

        public bool IsNodeSelectable(Guid nodeId) => Snapshot?.SelectableNodeIds.Contains(nodeId) ?? false;

        public bool IsNodeAudioSelectable(Guid nodeId) =>
            Snapshot is { } s && s.AudioNodeCounts.TryGetValue(nodeId, out var count) && count > 0;

        /// <summary>
        /// The live Folder Selection, whose roll-up denominators are the ones the last snapshot
        /// published. Rows read it; only an intent moves it.
        /// </summary>
        public FolderSelection Selection { get; private set; } = null!;

        /// <summary>The live Audio Item Selection, on the same terms as <see cref="Selection"/>.</summary>
        public AudioItemSelection AudioSelection { get; private set; } = null!;

        public ProjectFolderId? CurrentFolder => projection.Folder;

        public int SelectedParagraphCount => Selection?.SelectedParagraphCount ?? 0;
        public int SelectedAudioItemCount => AudioSelection?.SelectedItemCount ?? 0;

        // ---------------------------------------------------------------
        // Hierarchy rendering — branches and expansion, both from the snapshot
        // ---------------------------------------------------------------

        public bool IsExpanded(BookNodeLevel level, Guid nodeId) =>
            Snapshot?.Expansion.At(level).Contains(nodeId) ?? false;

        public IReadOnlyList<Part>? Parts(Guid volumeId) => Branch(Snapshot?.Branches.PartsByVolume, volumeId);
        public IReadOnlyList<Chapter>? Chapters(Guid partId) => Branch(Snapshot?.Branches.ChaptersByPart, partId);
        public IReadOnlyList<Paragraph>? Paragraphs(Guid chapterId) => Branch(Snapshot?.Branches.ParagraphsByChapter, chapterId);

        private static IReadOnlyList<T>? Branch<T>(IReadOnlyDictionary<Guid, IReadOnlyList<T>>? branches, Guid id) =>
            branches is not null && branches.TryGetValue(id, out var loaded) ? loaded : null;

        /// <summary>
        /// Whether a node is waiting on the build its expand gesture started. Liveness of a gesture in
        /// flight, not Book View state: it exists only between the click and the snapshot that answers
        /// it, which is exactly the window no snapshot can describe.
        /// </summary>
        public bool IsExpanding(Guid nodeId) => _expanding.Contains(nodeId);

        private readonly HashSet<Guid> _expanding = [];

        public async Task SetNodeExpandedAsync(BookNodeLevel level, Guid nodeId, bool expanded)
        {
            if (expanded)
            {
                _expanding.Add(nodeId);
                NotifyStateChanged();
            }

            try
            {
                await projection.ApplyAsync(new BookViewIntent.SetNodeExpanded(level, nodeId, expanded));
            }
            finally
            {
                // The snapshot that answers the gesture is published while the spinner is still up,
                // so taking it down needs a repaint of its own — including when the intent changed
                // nothing and published no snapshot at all.
                if (_expanding.Remove(nodeId))
                    NotifyStateChanged();
            }
        }

        // ---------------------------------------------------------------
        // Transient gestures — every one of them an intent
        // ---------------------------------------------------------------

        public Task SetViewModeAsync(BookViewMode mode) =>
            projection.ApplyAsync(new BookViewIntent.SetViewMode(mode));

        public Task TogglePlayingAudioItemAsync(Guid itemId) =>
            projection.ApplyAsync(new BookViewIntent.TogglePlayback(itemId));

        public Task ToggleParagraphAsync(Guid paragraphId, ParagraphSelection ancestry, bool on) =>
            projection.ApplyAsync(new BookViewIntent.SetParagraphSelected(paragraphId, ancestry, on));

        public Task SetNodeAsync(BookNodeLevel level, Guid nodeId, bool on, bool unprocessedOnly = false) =>
            projection.ApplyAsync(new BookViewIntent.SetNodeParagraphsSelected(level, nodeId, on, unprocessedOnly));

        public Task SetBulkAssignAsync(bool armed) =>
            projection.ApplyAsync(new BookViewIntent.SetBulkAssign(armed));

        public Task ToggleAudioItemAsync(AudioItemRef item, bool on) =>
            projection.ApplyAsync(new BookViewIntent.SetAudioItemSelected(item, on));

        public Task SetAudioNodeAsync(BookNodeLevel level, Guid nodeId, bool on, bool needsAudioOnly = false) =>
            projection.ApplyAsync(new BookViewIntent.SetNodeAudioItemsSelected(level, nodeId, on, needsAudioOnly));

        public Task AddSelectionToCharacterQueueAsync() =>
            projection.ApplyAsync(new BookViewIntent.QueueSelectedParagraphs());

        public Task AddSelectionToAudioQueueAsync() =>
            projection.ApplyAsync(new BookViewIntent.QueueSelectedAudioItems());

        // ---------------------------------------------------------------
        // Opening
        // ---------------------------------------------------------------

        private bool _characterQueueSubscribed;
        private bool _snapshotSubscribed;

        public async Task LoadAsync(ProjectFolderId folderId)
        {
            IsLoading = true;
            Subscribe();
            ConfirmReread = false;

            var snapshot = await projection.OpenAsync(folderId);

            Selection = selectionState.For(folderId);
            AudioSelection = audioSelectionState.For(folderId);
            SeedDerivedServices(folderId, snapshot);

            IsLoading = false;
            NotifyStateChanged();
        }

        /// <summary>
        /// Hands the snapshot's persisted half to the two singletons that combine it with live queue
        /// progress. Neither is revision-stamped state, which is why they are seeded from a snapshot
        /// rather than carried on one.
        /// </summary>
        private void SeedDerivedServices(ProjectFolderId folderId, BookViewSnapshot snapshot)
        {
            if (snapshot.HasContent)
                audioReviews.Hydrate(folderId, [.. snapshot.Reviews.Select(r => (r.Key, r.Value))]);

            nodeStatus.Seed(folderId, snapshot.NodeStatus);
        }

        /// <summary>
        /// The Book View has converged on someone else's committed change. The two singletons that
        /// mix persisted counts with live queue progress are reseeded from the snapshot that change
        /// produced — they are the only Book View state a published snapshot does not carry — and the
        /// page repaints.
        /// <para>
        /// The notice is shown exactly when the projection says so. Whether a change is surprising
        /// enough to mention is a reconciliation rule, not a MudBlazor one, so this adapter reads the
        /// verdict rather than recomputing it (ADR 0007).
        /// </para>
        /// <para>
        /// Raised on the projection's pump rather than the circuit's thread, like the queue events
        /// above it. Both things it touches are built for that: MudBlazor's snackbar provider
        /// marshals its own render, and <see cref="StateChanged"/> is answered by a component that
        /// wraps <c>StateHasChanged</c> in <c>InvokeAsync</c>.
        /// </para>
        /// </summary>
        private void OnExternalUpdate(BookViewExternalUpdate update)
        {
            SeedDerivedServices(update.Snapshot.Folder, update.Snapshot);

            if (update.Announce)
                snackbar.Add("Book updated elsewhere", Severity.Info);

            NotifyStateChanged();
        }

        // ---------------------------------------------------------------
        // Imports and rereads
        // ---------------------------------------------------------------

        /// <summary>Reads the project's source file into a Book that has none yet.</summary>
        public Task ReadBookAsync(ProjectFolderId folderId) =>
            ImportAsync(folderId, commit => bookUseCases.ImportAsync(folderId, reread: false, commit));

        /// <summary>Throws the Book's content away and reads the source file again in its place.</summary>
        public Task ConfirmRereadAsync(ProjectFolderId folderId) =>
            ImportAsync(folderId, commit => bookUseCases.ImportAsync(folderId, reread: true, commit));

        /// <summary>The same replacement, re-split under options the reader chooses in a dialog.</summary>
        public async Task ManualRereadAsync(ProjectFolderId folderId)
        {
            var dialog = await dialogService.ShowAsync<Shared.ManualRereadDialog>("Manual Reread Book");
            var result = await dialog.Result;
            if (result?.Canceled != false) return;

            if (result.Data is not ManualReadOptions options) return;

            await ImportAsync(folderId, commit => bookUseCases.ImportManuallyAsync(folderId, options, commit));
        }

        /// <summary>
        /// Runs one import and shows what it did. There is no reload afterwards, and nothing to
        /// reset: the replacement commits as one Book mutation, and the projection that commits it
        /// rebuilds this circuit's Book View — dropping the selections and expansion that pointed at
        /// content the reread deleted — before the gesture returns (ADR 0007).
        /// </summary>
        private async Task ImportAsync(
            ProjectFolderId folderId, Func<CommitBookMutation, Task<BookImportOutcome>> import)
        {
            IsBusy = true;
            Error = null;
            NotifyStateChanged();
            try
            {
                Error = (await import(CommitImportAsync)).Error;
            }
            finally
            {
                IsBusy = false;
                NotifyStateChanged();
            }
        }

        /// <summary>
        /// Where an import's mutation commits: this circuit's own projection, so the reader who asked
        /// for the reread waits for their Book View rather than being notified of it as someone
        /// else's change. The producer above needs the write-side outcome back, because only it knows
        /// whether the artifacts it staged were taken.
        /// </summary>
        private async Task<BookMutationOutcome> CommitImportAsync(BookMutation mutation, CancellationToken ct)
        {
            var outcome = await projection.MutateAsync(mutation, ct);
            switch (outcome)
            {
                case BookViewMutationOutcome.Coherent coherent:
                    SeedDerivedServices(coherent.Snapshot.Folder, coherent.Snapshot);
                    return new BookMutationOutcome.Committed(coherent.Receipt);
                case BookViewMutationOutcome.Uncommitted uncommitted:
                    return new BookMutationOutcome.Rejected(uncommitted.Reason, uncommitted.Message);
                // Committed is what the producer above needs to hear: the Book names the artifacts it
                // staged, so they must be kept. Only this circuit's view of the import is missing,
                // and the stale banner answers that. Nothing to reseed either — a stale outcome
                // published no new reads, so the two singletons still hold what they were given.
                case BookViewMutationOutcome.CommittedButStale stale:
                    return new BookMutationOutcome.Committed(stale.Receipt);
                case BookViewMutationOutcome.NoChange:
                    return new BookMutationOutcome.NoChange();
                default:
                    throw new NotSupportedException($"Unhandled Book View outcome {outcome.GetType().Name}.");
            }
        }

        /// <summary>
        /// Commits one Book mutation. The Book View is already coherent by the time this returns —
        /// the projection rebuilt it from the receipt — so there is nothing here to refresh, reseed
        /// or re-select afterwards (ADR 0007).
        /// <para>
        /// Only a refusal is worth telling the producer about, as a message. A coherent success
        /// needs no announcement, because the Book View in front of them already shows it, and a
        /// gesture that changed nothing has nothing to announce.
        /// </para>
        /// </summary>
        /// <returns>
        /// The outcome, for the few gestures that report their own success — a bulk assign says how
        /// much it moved. Most callers ignore it: the Book View is the report.
        /// </returns>
        public async Task<BookViewMutationOutcome> MutateAsync(BookMutation mutation)
        {
            IsBusy = true;
            Error = null;
            NotifyStateChanged();
            try
            {
                var outcome = await projection.MutateAsync(mutation);
                switch (outcome)
                {
                    case BookViewMutationOutcome.Coherent coherent:
                        SeedDerivedServices(coherent.Snapshot.Folder, coherent.Snapshot);
                        break;
                    // Both arms say the change was kept, because the one thing the reader must not
                    // conclude is that it was lost — they would make it a second time. They differ
                    // on what to do next, and only one of them has a Refresh to point at.
                    case BookViewMutationOutcome.CommittedButStale { Snapshot: null }:
                        // They switched projects while it was committing. Nothing is stale: this
                        // Book View is a coherent view of a different Book.
                        snackbar.Add("Your change was saved to the book you moved away from.", Severity.Info);
                        break;
                    case BookViewMutationOutcome.CommittedButStale:
                        snackbar.Add(
                            "Your change was saved, but this Book View could not be refreshed. " +
                            "Use Refresh to reload it — do not repeat the change.",
                            Severity.Warning);
                        break;
                    case BookViewMutationOutcome.Uncommitted uncommitted:
                        snackbar.Add(uncommitted.Message, Severity.Warning);
                        break;
                }
                return outcome;
            }
            finally
            {
                IsBusy = false;
                NotifyStateChanged();
            }
        }

        /// <summary>
        /// The recovery behind the stale banner: rebuild this Book View from the persisted Book. A
        /// retry that fails again leaves the banner up rather than clearing it and hoping — the
        /// projection is still stale, and the reader is told the refresh did not take.
        /// </summary>
        public async Task RetryRebuildAsync()
        {
            // A queued click, or a convergence that recovered the projection while the banner was
            // still on screen: there is nothing left to retry, and the projection refuses by
            // throwing — which a Blazor event handler has nowhere to put.
            if (!IsStale) return;

            IsBusy = true;
            NotifyStateChanged();
            try
            {
                if (await projection.RetryRebuildAsync() is { Health: BookViewHealth.Stale })
                    snackbar.Add("The Book View still could not be refreshed.", Severity.Warning);
            }
            finally
            {
                IsBusy = false;
                NotifyStateChanged();
            }
        }

        // ---------------------------------------------------------------
        // Book mutations — the families still on the legacy command path
        // ---------------------------------------------------------------

        /// <summary>
        /// The single front door behind every character chip. Chips render identically in both modes and
        /// read <see cref="FolderSelection.BulkMode"/> nowhere — they hand over the row and, for an item
        /// chip, the item, and this decides. A pick on a selected row with bulk mode armed fans out across
        /// the whole selection, whichever chip fired; anything else is a single assign.
        /// </summary>
        /// <param name="item">Null for the paragraph chip, the item for an item chip.</param>
        public Task AssignCharacterAsync(
            ProjectFolderId folderId, Paragraph paragraph, ParagraphItem? item, Guid? characterId)
        {
            if (Selection.BulkMode && Selection.IsParagraphSelected(paragraph.Id))
                return AssignCharacterToSelectionAsync(folderId, characterId);

            return item is null
                ? SetParagraphCharacterAsync(folderId, paragraph, characterId)
                : SetItemCharacterAsync(folderId, item, characterId);
        }

        public Task SetItemCharacterAsync(ProjectFolderId folderId, ParagraphItem item, Guid? characterId)
        {
            // Queue state, not Book data: the paragraph's last outcome describes an attempt the user
            // has just answered by hand, so it stops being worth showing.
            characterQueue.ClearOutcome(folderId, item.ParagraphId);

            return MutateAsync(new SetItemSpeakerMutation(folderId, item.Id, characterId));
        }

        public Task SetParagraphCharacterAsync(ProjectFolderId folderId, Paragraph paragraph, Guid? characterId)
        {
            characterQueue.ClearOutcome(folderId, paragraph.Id);

            return MutateAsync(new SetParagraphSpeakerMutation(folderId, paragraph.Id, characterId));
        }

        /// <summary>
        /// Bulk apply: one character — or a clear, when <paramref name="characterId"/> is null — across
        /// every Character item in every selected paragraph, behind one confirm.
        /// <para>
        /// What becomes of the selection afterwards is not decided here: the projection recomputes it
        /// against the new revision (ADR 0007), so an ordinary fan-out — which moves no denominator —
        /// keeps it and leaves the dock bar up, while a paragraph the assign turned all-narration
        /// drops out of it with the roll-up it was counted in.
        /// </para>
        /// </summary>
        public async Task AssignCharacterToSelectionAsync(ProjectFolderId folderId, Guid? characterId)
        {
            var ids = Selection.SelectedParagraphIds().ToList();
            var preview = await reader.GetBulkAssignPreviewAsync(folderId, ids);

            if (preview.ParagraphsWithCharacterItems == 0)
            {
                snackbar.Add("No dialog in the selection — nothing to assign.", Severity.Info);
                return;
            }

            // Resolved before the confirm, not after, because the dialog quotes the character's name.
            var character = ResolveCharacter(characterId);

            var items = preview.CharacterItems;
            var paras = preview.ParagraphsWithCharacterItems;
            // Selected paragraphs the write will not touch: all narration and pauses.
            var skipped = ids.Count - paras;

            // Null name means a clear throughout the wording. Keyed on characterId, not on the
            // resolved entity, so an id the roster still cannot explain reads as an assign.
            var name = characterId.HasValue ? character?.Name ?? "the character" : null;

            if (!await dialogService.ConfirmAsync(
                    BulkConfirmTitle(name),
                    BulkConfirmMessage(name, items, paras, skipped),
                    name is null ? "Clear" : "Assign"))
                return;

            foreach (var id in ids)
                characterQueue.ClearOutcome(folderId, id);

            // One mutation for the whole batch, not one per paragraph. Nothing is patched afterwards:
            // the projection republishes the affected rows, the counts they roll up into, and the
            // Folder Selection recomputed against the new revision — a paragraph that has stopped
            // being a Character paragraph drops out of the selection with its denominator (ADR 0007).
            if (await MutateAsync(new SetParagraphsSpeakerMutation(folderId, ids, characterId))
                is not BookViewMutationOutcome.Coherent)
                return;

            snackbar.Add(
                name is null
                    ? $"Cleared speakers on {items} lines in {paras} paragraphs."
                    : $"Assigned {name} to {items} lines in {paras} paragraphs.",
                Severity.Success);
        }

        /// <summary>
        /// The roster entry behind a picked id, straight off the published snapshot. Null for a clear,
        /// and null too for an id the roster cannot explain — which the confirm wording handles
        /// rather than reaching for a read.
        /// <para>
        /// It needs no refresh of its own even on the add-new path: creating a Character is a Book
        /// mutation, so the roster this reads was republished by the reconciliation that gesture
        /// already waited for (ADR 0007).
        /// </para>
        /// </summary>
        private Character? ResolveCharacter(Guid? characterId) =>
            characterId is { } id ? Characters.FirstOrDefault(c => c.Id == id) : null;

        private static string BulkConfirmTitle(string? name) =>
            name is null ? "Clear speakers in selection" : $"Assign {name} to selection";

        private static string BulkConfirmMessage(string? name, int items, int paras, int skipped)
        {
            var scope = $"{items} dialog line{Plural(items)} in {paras} paragraph{Plural(paras)}";

            var message = name is null
                ? $"{scope} lose their speaker and need attributing again."
                : $"{name} becomes the speaker for {scope}. Existing speakers are replaced.";

            if (skipped == 0) return message;

            return message + $" {skipped} selected paragraph{Plural(skipped)} have no dialog and stay unchanged.";
        }

        /// <summary>Noun-suffix pluralisation only, the idiom the confirm wordings are written in.</summary>
        private static string Plural(int n) => n == 1 ? "" : "s";

        /// <summary>
        /// Adds a Character from the chip menu and answers with the id to assign, so the gesture that
        /// invented a speaker can go straight on to stamping them.
        /// <para>
        /// A name the roster already answers to — its own or an alias — creates nobody and is not a
        /// failure: the id wanted is whoever already goes by it, and finding them costs the one read
        /// that path needs, because the snapshot's roster carries no aliases. Nothing is refreshed
        /// here: the mutation's own reconciliation already did (ADR 0007).
        /// </para>
        /// </summary>
        public async Task<Guid?> AddCharacterAsync(ProjectFolderId folderId)
        {
            var dialog = await dialogService.ShowAsync<Shared.Characters.AddCharacterDialog>("Add Character");
            var result = await dialog.Result;
            if (result?.Canceled != false) return null;
            if (result.Data is not string name || string.IsNullOrWhiteSpace(name)) return null;

            var trimmed = name.Trim();
            return await MutateAsync(new CreateCharacterMutation(folderId, trimmed)) switch
            {
                BookViewMutationOutcome.Coherent coherent => coherent.Receipt.Effects.CreatedId,
                BookViewMutationOutcome.NoChange => await characterRoster.FindAsync(folderId, trimmed),
                _ => null,
            };
        }

        public Task AddBookTitleAsync(ProjectFolderId folderId) =>
            MutateAsync(new AddBookTitleMutation(folderId));

        public Task AddVolumeTitlesAsync(ProjectFolderId folderId) =>
            MutateAsync(new AddVolumeTitlesMutation(folderId));

        public Task AddPartTitlesAsync(ProjectFolderId folderId) =>
            MutateAsync(new AddPartTitlesMutation(folderId));

        public Task AddChapterTitlesAsync(ProjectFolderId folderId) =>
            MutateAsync(new AddChapterTitlesMutation(folderId));

        public Task AddPausesAsync(ProjectFolderId folderId) =>
            MutateAsync(new AddPausesMutation(folderId));

        public void RequestConfirmReread()
        {
            ConfirmReread = true;
            NotifyStateChanged();
        }

        public void CancelConfirmReread()
        {
            ConfirmReread = false;
            NotifyStateChanged();
        }

        /// <summary>
        /// Silences one item's review. The badge and the chip both come back on the snapshot the
        /// mutation reconciles, so there is nothing to patch here — they cannot disagree.
        /// </summary>
        public Task DismissAudioReviewAsync(ProjectFolderId folderId, Guid paragraphItemId) =>
            MutateAsync(new DismissAudioReviewMutation(folderId, paragraphItemId));

        private void NotifyStateChanged() => StateChanged?.Invoke();

        /// <summary>
        /// A bulk write must never meet an in-flight attribution, so any armed bulk mode is turned
        /// off — not merely greyed out — the moment the character queue has work.
        /// </summary>
        private async void DisarmBulkIfQueueBusy()
        {
            // An event handler, so it has nowhere to throw: a projection that is not open yet has no
            // armed bulk mode to disarm either.
            if (CurrentFolder is null) return;
            if (characterQueue.Snapshot().IsBusy && Selection is { BulkMode: true })
                await SetBulkAssignAsync(false);
        }

        private void Subscribe()
        {
            if (!_characterQueueSubscribed)
            {
                characterQueue.Changed += DisarmBulkIfQueueBusy;
                _characterQueueSubscribed = true;
            }

            // Every snapshot repaints the Book View, whichever gesture or rebuild produced it.
            if (!_snapshotSubscribed)
            {
                projection.SnapshotPublished += NotifyStateChanged;
                projection.ExternalUpdateApplied += OnExternalUpdate;
                _snapshotSubscribed = true;
            }
        }

        public void Dispose()
        {
            if (_characterQueueSubscribed)
            {
                characterQueue.Changed -= DisarmBulkIfQueueBusy;
                _characterQueueSubscribed = false;
            }

            if (_snapshotSubscribed)
            {
                projection.SnapshotPublished -= NotifyStateChanged;
                projection.ExternalUpdateApplied -= OnExternalUpdate;
                _snapshotSubscribed = false;
            }
        }
    }
}
