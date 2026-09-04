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
    /// that tickets 04 and after move onto receipts.
    /// </para>
    /// </summary>
    public class BookHierarchyPresenter(
        IProjectReader reader,
        BookViewProjection projection,
        IBookCommandHandler commandHandler,
        BookUseCases bookUseCases,
        BookTreeState treeState,
        BookSelectionState selectionState,
        AudioItemSelectionState audioSelectionState,
        IDialogService dialogService,
        ISnackbar snackbar,
        CharacterQueueService characterQueue,
        AudioQueueService audioQueue,
        AudioReviewService audioReviews,
        NodeStatusService nodeStatus,
        EventBroadcaster<ParagraphItemsChanged> paragraphItemsChanged) : IDisposable
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

        private bool _audioQueueSubscribed;
        private bool _itemsChangedSubscribed;
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

        public async Task ResetAndLoadAsync(ProjectFolderId folderId)
        {
            selectionState.Reset(folderId);
            audioSelectionState.Reset(folderId);
            nodeStatus.Clear(folderId);
            await LoadAsync(folderId);
        }

        /// <summary>
        /// Rebuilds the Book View from the persisted Book after a write made elsewhere on the page.
        /// The interim path only: from ticket 04 on a mutation's receipt reconciles the projection and
        /// nothing asks for this by hand.
        /// </summary>
        public async Task RefreshAsync()
        {
            if (projection.Folder is not { } folderId) return;

            SeedDerivedServices(folderId, await projection.RebuildAsync());
            NotifyStateChanged();
        }

        public Task ReadBookAsync(ProjectFolderId folderId) =>
            ExecuteAndReloadAsync(folderId, () => bookUseCases.ImportAsync(folderId), reset: false);

        public async Task ConfirmRereadAsync(ProjectFolderId folderId) =>
            await ExecuteAndReloadAsync(folderId, () => bookUseCases.ImportAsync(folderId, reread: true), reset: true);

        public async Task ManualRereadAsync(ProjectFolderId folderId)
        {
            var dialog = await dialogService.ShowAsync<Shared.ManualRereadDialog>("Manual Reread Book");
            var result = await dialog.Result;
            if (result?.Canceled != false) return;

            var options = result.Data as ManualReadOptions;
            if (options is null) return;

            await ExecuteAndReloadAsync(folderId,
                () => bookUseCases.ImportManuallyAsync(folderId, options), reset: true);
        }

        public enum SplitLevel { Volume, Part, Chapter, Paragraph }

        public async Task SplitAndReloadAsync(
            ProjectFolderId folderId, BookCommand command, SplitLevel level, Guid sourceParentId)
        {
            var newId = await commandHandler.ExecuteAsync(command);
            if (newId is Guid created)
                treeState.For(folderId).MarkSplitExpansion(level switch
                {
                    SplitLevel.Volume => BookNodeLevel.Volume,
                    SplitLevel.Part => BookNodeLevel.Part,
                    _ => BookNodeLevel.Chapter,
                }, sourceParentId, created);
            await ResetAndLoadAsync(folderId);
        }

        /// <summary>
        /// Keeps the reader's place across a merge: whatever was open on the node that went away is
        /// open on the survivor. Structural continuity of expansion intent rather than a gesture,
        /// which is why it is not an intent — the reader expanded nothing.
        /// </summary>
        public void NoteMerged(ProjectFolderId folderId, Guid survivorId, Guid deletedId) =>
            treeState.For(folderId).FixMergeExpansion(survivorId, deletedId);

        // ---------------------------------------------------------------
        // Book mutations — tickets 04 and after move these onto receipts
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

        /// <summary>
        /// Mirrors, in the loaded tree, what <c>UpdateParagraphItemTextCommand</c> has just written:
        /// the edited item's audio is gone and any verdict on it deleted. Called only after the
        /// command executed and only when the text actually changed, so an edit that changed nothing
        /// leaves good audio and its review alone.
        /// <para>
        /// Without this the row keeps rendering a WAV the database no longer records — the audio
        /// checkbox stays disabled, a "select needs audio" pass keeps skipping the item, and the
        /// chapter's audio-remaining badge stays a count too low until the next full load.
        /// </para>
        /// </summary>
        public async Task NoteItemTextEditedAsync(ProjectFolderId folderId, ParagraphItem item)
        {
            item.AudioFileName = null;
            audioReviews.Clear(folderId, item.Id);
            RecomputeParagraphReview(folderId, item.Id);

            // One read, same as a speaker flip: the audio denominator moves and there is no
            // increment-side patch on NodeStatusService to move it with.
            nodeStatus.Seed(folderId, await reader.GetNodeStatusSeedAsync(folderId));

            NotifyStateChanged();
        }

        public async Task SetItemCharacterAsync(ProjectFolderId folderId, ParagraphItem item, Guid? characterId)
        {
            await commandHandler.ExecuteAsync(new SetItemCharacterCommand(folderId, item.Id, characterId));
            characterQueue.ClearOutcome(folderId, item.ParagraphId);

            var character = await ResolveCharacterAsync(folderId, characterId);

            item.CharacterId = characterId;
            item.Character = character;
            item.AudioFileName = null;   // a hand-flip discards the item's audio (ADR-0006)

            // The reseed ends in a rebuild, and every published snapshot repaints the view.
            await ReseedAfterSpeakerChangeAsync(folderId);
        }

        public async Task SetParagraphCharacterAsync(ProjectFolderId folderId, Paragraph paragraph, Guid? characterId)
        {
            characterQueue.ClearOutcome(folderId, paragraph.Id);

            var character = characterId.HasValue
                ? Characters.FirstOrDefault(c => c.Id == characterId.Value)
                : null;

            await commandHandler.ExecuteAsync(new SetParagraphCharacterCommand(folderId, paragraph.Id, characterId));
            ParagraphCharacterStamp.Apply(paragraph.Items, characterId, character, sweepAllNarrationParagraph: true);

            // The reseed ends in a rebuild, and every published snapshot repaints the view.
            await ReseedAfterSpeakerChangeAsync(folderId);
        }

        /// <summary>
        /// Bulk apply: one character — or a clear, when <paramref name="characterId"/> is null — across
        /// every Character item in every selected paragraph, behind one confirm. The selection is kept,
        /// so the dock bar stays up and bulk mode stays armed.
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

            // Resolved before the confirm, not after, because the dialog quotes the character's name:
            // on the add-new path the id can be newer than the roster. A read, so a cancelled confirm
            // still writes nothing.
            var character = await ResolveCharacterAsync(folderId, characterId);

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

            await commandHandler.ExecuteAsync(new SetParagraphsCharacterCommand(folderId, ids, characterId));

            // Walk the loaded paragraphs testing membership, never the selection looking ids up — the
            // selection can dwarf what is in memory. Unloaded paragraphs need nothing: their chapter
            // reads the committed write when it expands. Done before the reseed, so the reseed's
            // republish cannot land mid-walk — and before the selection is cleared, since the walk
            // reads it.
            foreach (var p in LoadedParagraphs())
            {
                if (Selection.IsParagraphSelected(p.Id))
                    ParagraphCharacterStamp.Apply(p.Items, characterId, character);
            }

            // One reseed for the whole batch, not one per item.
            // The reseed ends in a rebuild, and every published snapshot repaints the view.
            await ReseedAfterSpeakerChangeAsync(folderId);

            snackbar.Add(
                name is null
                    ? $"Cleared speakers on {items} lines in {paras} paragraphs."
                    : $"Assigned {name} to {items} lines in {paras} paragraphs.",
                Severity.Success);
        }

        /// <summary>
        /// A speaker change can flip whether a paragraph is a Character paragraph at all — assign its
        /// last dialog item to the narrator and it stops being one; give a narration item to a
        /// character and it starts (ADR-0006). Selectable nodes, roll-up denominators and the status
        /// badges are all derived from that, so they are reseeded from the reader rather than patched
        /// incrementally: mixed-structure denominators have been a bug source before, and there is no
        /// precedent for patching "paragraph becomes / stops being attributable".
        /// <para>
        /// Selection is cleared only when the denominators actually moved, so a roll-up checkbox can
        /// never mix totals from before and after the edit — while an ordinary bulk assign, which
        /// changes no membership, keeps its selection and leaves the dock bar up. Recomputing
        /// selection safety is reconciliation work the projection takes over from ticket 04 on; until
        /// a receipt carries this write, the presenter still asks.
        /// </para>
        /// </summary>
        private async Task ReseedAfterSpeakerChangeAsync(ProjectFolderId folderId)
        {
            var was = Snapshot?.NodeCharacterParagraphCounts ?? new Dictionary<Guid, int>();
            var overview = await reader.GetBookOverviewAsync(folderId);

            var moved = overview.NodeCharacterParagraphCounts.Count != was.Count
                || overview.NodeCharacterParagraphCounts.Any(kv =>
                    !was.TryGetValue(kv.Key, out var before) || before != kv.Value);

            if (moved) Selection.Clear();

            // The counts, the selectable nodes, the badges and the voice previews all moved with the
            // write: one rebuild reads them together rather than four copies being patched apart.
            SeedDerivedServices(folderId, await projection.RebuildAsync());
        }

        /// <summary>
        /// The roster entry behind a picked id, rebuilding the Book View when the id is newer than the
        /// roster — the add-new path, where the character was created after the snapshot was
        /// published. Null for a clear.
        /// </summary>
        private async Task<Character?> ResolveCharacterAsync(ProjectFolderId folderId, Guid? characterId)
        {
            if (characterId is not { } id) return null;

            var character = Characters.FirstOrDefault(c => c.Id == id);
            if (character is not null) return character;

            await projection.RebuildAsync();
            return Characters.FirstOrDefault(c => c.Id == id);
        }

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

        public async Task<Guid?> AddCharacterAsync(ProjectFolderId folderId)
        {
            var dialog = await dialogService.ShowAsync<Shared.Characters.AddCharacterDialog>("Add Character");
            var result = await dialog.Result;
            if (result?.Canceled != false) return null;
            if (result.Data is not string name || string.IsNullOrWhiteSpace(name)) return null;

            var newId = await commandHandler.ExecuteAsync(new CreateCharacterCommand(folderId, name.Trim()));
            await projection.RebuildAsync();
            return newId as Guid?;
        }

        public Task AddBookTitleAsync(ProjectFolderId folderId) =>
            ExecuteCommandAndReloadAsync(folderId, new AddBookTitleCommand(folderId));

        public Task AddVolumeTitlesAsync(ProjectFolderId folderId) =>
            ExecuteCommandAndReloadAsync(folderId, new AddVolumeTitlesCommand(folderId));

        public Task AddPartTitlesAsync(ProjectFolderId folderId) =>
            ExecuteCommandAndReloadAsync(folderId, new AddPartTitlesCommand(folderId));

        public Task AddChapterTitlesAsync(ProjectFolderId folderId) =>
            ExecuteCommandAndReloadAsync(folderId, new AddChapterTitlesCommand(folderId));

        public Task AddPausesAsync(ProjectFolderId folderId) =>
            ExecuteCommandAndReloadAsync(folderId, new AddPausesCommand(folderId));

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

        public async Task DismissAudioReviewAsync(ProjectFolderId folderId, Guid paragraphItemId)
        {
            await commandHandler.ExecuteAsync(new DismissAudioReviewCommand(folderId, paragraphItemId));

            var current = audioReviews.ReviewOf(folderId, paragraphItemId);
            if (current is not null)
                audioReviews.Set(folderId, paragraphItemId,
                    current with { State = AudioReviewState.Dismissed });

            RecomputeParagraphReview(folderId, paragraphItemId);
            NotifyStateChanged();
        }

        private void RecomputeParagraphReview(ProjectFolderId folderId, Guid paragraphItemId)
        {
            var para = LoadedOwnerOf(paragraphItemId);
            if (para is null) return;

            var hasAnyNeedsReview = para.Items.Any(i =>
                audioReviews.ReviewOf(folderId, i.Id)?.State == AudioReviewState.NeedsReview);
            nodeStatus.OnReviewChanged(folderId, para.Id, hasAnyNeedsReview);
        }

        /// <summary>The paragraphs the snapshot has loaded — the only ones a patch can be seen in.</summary>
        private IEnumerable<Paragraph> LoadedParagraphs() => Snapshot?.Branches.AllParagraphs() ?? [];

        private Paragraph? LoadedOwnerOf(Guid paragraphItemId) =>
            LoadedParagraphs().FirstOrDefault(p => p.Items.Any(i => i.Id == paragraphItemId));

        private async Task ExecuteAndReloadAsync(
            ProjectFolderId folderId,
            Func<Task<Result>> operation,
            bool reset)
        {
            IsBusy = true;
            Error = null;
            NotifyStateChanged();
            var result = await operation();
            Error = result.IsSuccess ? null : result.Error;
            if (result.IsSuccess)
                await (reset ? ResetAndLoadAsync(folderId) : LoadAsync(folderId));
            IsBusy = false;
            NotifyStateChanged();
        }

        private Task ExecuteCommandAndReloadAsync(ProjectFolderId folderId, BookCommand command) =>
            ExecuteAndReloadAsync(folderId, async () =>
            {
                await commandHandler.ExecuteAsync(command);
                return Result.Ok();
            }, reset: true);

        private void NotifyStateChanged() => StateChanged?.Invoke();

        /// <summary>The node-status counter unit: speech items still without a speaker (ADR-0006).</summary>
        private static int CountUnattributed(IEnumerable<ParagraphItem>? items) =>
            items?.Count(i => !ParagraphItemKinds.IsPause(i.ItemType) && i.CharacterId is null) ?? 0;

        /// <summary>
        /// A paragraph's items changed (attribution stamped speakers, or an item was stamped by
        /// hand). Any number of items can change in one event, so the whole item list is reloaded
        /// rather than a single stamp patched.
        /// </summary>
        private async void OnParagraphItemsChanged(ParagraphItemsChanged e)
        {
            if (CurrentFolder is not { } current || current != e.FolderId) return;

            var para = LoadedParagraphs().FirstOrDefault(p => p.Id == e.ParagraphId);
            if (para is null) return;

            var children = await reader.GetChildrenAsync(e.FolderId, BookNodeLevel.Chapter, para.ChapterId);
            var reloaded = children?.Paragraphs?.FirstOrDefault(p => p.Id == e.ParagraphId);
            if (reloaded is null) return;

            para.Items = reloaded.Items;

            nodeStatus.OnCharacterAttributed(e.FolderId, e.ParagraphId, CountUnattributed(para.Items));

            // Attribution can create characters, and it moves the Voice each stamped line would be
            // spoken in, so the roster and the previews are republished rather than patched.
            await projection.RebuildAsync();
        }

        private void OnAudioFileAssigned(ProjectFolderId folder, Guid paragraphItemId, string relativePath)
        {
            if (CurrentFolder is not { } current || current != folder) return;

            var para = LoadedOwnerOf(paragraphItemId);
            if (para is null) return;

            var item = para.Items.First(i => i.Id == paragraphItemId);
            item.AudioFileName = relativePath;
            nodeStatus.OnAudioAssigned(folder, item.ParagraphId);
        }

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
            if (!_audioQueueSubscribed)
            {
                audioQueue.AudioFileAssigned += OnAudioFileAssigned;
                _audioQueueSubscribed = true;
            }

            if (!_itemsChangedSubscribed)
            {
                paragraphItemsChanged.Event += OnParagraphItemsChanged;
                _itemsChangedSubscribed = true;
            }

            if (!_characterQueueSubscribed)
            {
                characterQueue.Changed += DisarmBulkIfQueueBusy;
                _characterQueueSubscribed = true;
            }

            // Every snapshot repaints the Book View, whichever gesture or rebuild produced it.
            if (!_snapshotSubscribed)
            {
                projection.SnapshotPublished += NotifyStateChanged;
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

            if (_audioQueueSubscribed)
            {
                audioQueue.AudioFileAssigned -= OnAudioFileAssigned;
                _audioQueueSubscribed = false;
            }

            if (_itemsChangedSubscribed)
            {
                paragraphItemsChanged.Event -= OnParagraphItemsChanged;
                _itemsChangedSubscribed = false;
            }

            if (_snapshotSubscribed)
            {
                projection.SnapshotPublished -= NotifyStateChanged;
                _snapshotSubscribed = false;
            }
        }
    }
}
