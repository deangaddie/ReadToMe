using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MudBlazor;
using Read2Me.App.Shared;
using Read2Me.Core.Models;
using Read2Me.Data.Entities;
using Read2Me.Services;
using Read2Me.Services.Audio;
using Read2Me.Services.Characters;
using Read2Me.Services.Events;
using Read2Me.Services.NodeStatus;
using Read2Me.Services.UseCases;
using Read2Me.Services.Voice;

namespace Read2Me.App.State
{
    public class BookHierarchyPresenter(
        IProjectReader reader,
        IBookProjectLoader loader,
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
        IVoiceResolver voiceResolver,
        ISelectionCoordinator selectionCoordinator,
        EventBroadcaster<ParagraphItemsChanged> paragraphItemsChanged) : IDisposable
    {
        public bool IsLoading { get; private set; }
        public bool HasContent { get; private set; }
        public bool IsBusy { get; private set; }

        private BookViewMode _viewMode = BookViewMode.Combined;
        public BookViewMode ViewMode
        {
            get => _viewMode;
            set
            {
                if (_viewMode == value) return;
                _viewMode = value;
                Selection?.Clear();
                AudioSelection?.Clear();
                // Voice rules can be edited on another tab without a reload; clear
                // the preview cache on (re)entry so it re-resolves against the DB.
                if (value == BookViewMode.SplitAudio)
                    InvalidateVoicePreview();
                NotifyStateChanged();
            }
        }

        public bool SplitView => _viewMode != BookViewMode.Combined;

        public bool ConfirmReread { get; private set; }
        public string? Filename { get; private set; }
        public string? Error { get; private set; }
        public IReadOnlyList<Volume> Volumes { get; private set; } = [];
        public List<Character> Characters { get; private set; } = [];
        public int TotalParts { get; private set; }
        public int TotalChapters { get; private set; }
        public PerFolderState Tree { get; private set; } = null!;
        public FolderSelection Selection { get; private set; } = null!;
        public AudioItemSelection AudioSelection { get; private set; } = null!;

        public Guid? PlayingAudioItemId { get; private set; }
        public event Action? PlayingItemChanged;

        public void TogglePlayingAudioItem(Guid itemId)
        {
            PlayingAudioItemId = PlayingAudioItemId == itemId ? null : itemId;
            PlayingItemChanged?.Invoke();
        }

        private HashSet<Guid> _selectableNodes = [];
        private IReadOnlyDictionary<Guid, int> _nodeCounts = new Dictionary<Guid, int>();
        private IReadOnlyDictionary<Guid, int> _audioNodeCounts = new Dictionary<Guid, int>();

        public bool NarratorOnlyMode { get; private set; }

        private readonly Dictionary<Guid, string?> _resolvedVoiceNames = new();

        public string? ResolvedVoiceName(Guid itemId) =>
            _resolvedVoiceNames.TryGetValue(itemId, out var name) ? name : null;

        // Resolves voice names only for item ids not already cached, then merges
        // (does not replace). Cheap no-op once an item is resolved, so it is safe
        // to call from render — toggles no longer trigger resolver work.
        public async Task EnsureVoicePreviewAsync(ProjectFolderId folderId, IEnumerable<Guid> itemIds)
        {
            var missing = itemIds.Where(id => !_resolvedVoiceNames.ContainsKey(id)).ToList();
            if (missing.Count == 0) return;

            var names = await voiceResolver.ResolveNamesAsync(folderId, missing);
            foreach (var (id, name) in names)
                _resolvedVoiceNames[id] = name;
        }

        // Drops cached voice previews so the next render re-resolves. Call when
        // anything affecting voice selection changes (voice rules, attribution,
        // narrator-only mode, reload).
        public void InvalidateVoicePreview() => _resolvedVoiceNames.Clear();

        public bool IsNodeSelectable(Guid nodeId) => _selectableNodes.Contains(nodeId);
        public bool IsNodeAudioSelectable(Guid nodeId) => _audioNodeCounts.ContainsKey(nodeId) && _audioNodeCounts[nodeId] > 0;

        private ProjectFolderId? _lastFolder;
        private bool _audioQueueSubscribed;
        private bool _itemsChangedSubscribed;
        private bool _characterQueueSubscribed;

        public event Action? StateChanged;

        public async Task LoadAsync(ProjectFolderId folderId)
        {
            IsLoading = true;

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

            if (_lastFolder.HasValue && _lastFolder.Value.Value != folderId.Value)
            {
                selectionState.Reset(_lastFolder.Value);
                audioSelectionState.Reset(_lastFolder.Value);
            }

            _lastFolder = folderId;
            selectionCoordinator.SetCurrentFolder(folderId);
            Tree = treeState.For(folderId);
            Tree.Changed -= NotifyStateChanged;
            Tree.Changed += NotifyStateChanged;
            Selection = selectionState.For(folderId);
            AudioSelection = audioSelectionState.For(folderId);
            ConfirmReread = false;

            var snapshot = await loader.LoadSnapshotAsync(folderId);
            Filename = snapshot.Filename;
            HasContent = snapshot.HasContent;
            Volumes = snapshot.Volumes;
            Characters = snapshot.Characters;
            TotalParts = snapshot.TotalParts;
            TotalChapters = snapshot.TotalChapters;
            NarratorOnlyMode = snapshot.NarratorOnlyMode;
            _selectableNodes = snapshot.SelectableNodeIds;
            _nodeCounts = snapshot.NodeCharacterParagraphCounts;
            Selection.SetCounts(_nodeCounts);
            _audioNodeCounts = snapshot.AudioNodeCounts;
            AudioSelection.SetCounts(_audioNodeCounts);
            InvalidateVoicePreview();

            // Load audio-review flags from prior sessions so they surface on project open.
            if (snapshot.HasContent)
                audioReviews.Hydrate(folderId, snapshot.AudioReviews);

            nodeStatus.Seed(folderId, snapshot.NodeStatusSeed);

            if (Volumes.Count == 1)
                Tree.ExpandedVolumeIds.Add(Volumes[0].Id);

            await Tree.RestoreExpandedAsync();

            IsLoading = false;
            NotifyStateChanged();
        }

        public async Task ResetAndLoadAsync(ProjectFolderId folderId)
        {
            selectionState.Reset(folderId);
            audioSelectionState.Reset(folderId);
            nodeStatus.Clear(folderId);
            Tree?.Reset();
            await LoadAsync(folderId);
        }

        public Task ReadBookAsync(ProjectFolderId folderId) =>
            ExecuteAndReloadAsync(folderId, () => bookUseCases.ImportAsync(folderId), resetTree: false);

        public async Task ConfirmRereadAsync(ProjectFolderId folderId) =>
            await ExecuteAndReloadAsync(folderId, () => bookUseCases.ImportAsync(folderId, reread: true), resetTree: true);

        public async Task ManualRereadAsync(ProjectFolderId folderId)
        {
            var dialog = await dialogService.ShowAsync<Shared.ManualRereadDialog>("Manual Reread Book");
            var result = await dialog.Result;
            if (result?.Canceled != false) return;

            var options = result.Data as ManualReadOptions;
            if (options is null) return;

            await ExecuteAndReloadAsync(folderId,
                () => bookUseCases.ImportManuallyAsync(folderId, options), resetTree: true);
        }

        public enum SplitLevel { Volume, Part, Chapter, Paragraph }

        public async Task SplitAndReloadAsync(
            ProjectFolderId folderId, BookCommand command, SplitLevel level, Guid sourceParentId)
        {
            var newId = await commandHandler.ExecuteAsync(command);
            if (newId is Guid created)
                Tree.MarkSplitExpansion(level switch
                {
                    SplitLevel.Volume => Tree.ExpandedVolumeIds,
                    SplitLevel.Part => Tree.ExpandedPartIds,
                    _ => Tree.ExpandedChapterIds,
                }, sourceParentId, created);
            await ResetAndLoadAsync(folderId);
        }

        /// <summary>
        /// The single front door behind every character chip. Chips render identically in both modes and
        /// read <see cref="FolderSelection.BulkMode"/> nowhere — they hand over the row and, for a segment
        /// chip, the item, and this decides. A pick on a selected row with bulk mode armed fans out across
        /// the whole selection, whichever chip fired; anything else is a single assign.
        /// </summary>
        /// <param name="item">Null for the paragraph chip, the segment for an item chip.</param>
        public Task AssignCharacterAsync(
            ProjectFolderId folderId, Paragraph paragraph, ParagraphItem? item, Guid? characterId)
        {
            if (Selection.BulkMode && Selection.IsParagraphSelected(paragraph.Id))
                return AssignCharacterToSelectionAsync(folderId, characterId);

            return item is null
                ? SetParagraphCharacterAsync(folderId, paragraph, characterId)
                : SetItemCharacterAsync(folderId, item, characterId);
        }

        public async Task SetItemCharacterAsync(ProjectFolderId folderId, ParagraphItem item, Guid? characterId)
        {
            await commandHandler.ExecuteAsync(new SetItemCharacterCommand(folderId, item.Id, characterId));
            characterQueue.ClearOutcome(folderId, item.ParagraphId);

            var character = await ResolveCharacterAsync(folderId, characterId);

            item.CharacterId = characterId;
            item.Character = character;

            nodeStatus.OnCharacterAttributed(folderId, item.ParagraphId,
                CountUnattributed(item.Paragraph?.Items));

            InvalidateVoicePreview();
            NotifyStateChanged();
        }

        public async Task SetParagraphCharacterAsync(ProjectFolderId folderId, Paragraph paragraph, Guid? characterId)
        {
            characterQueue.ClearOutcome(folderId, paragraph.Id);

            var character = characterId.HasValue ? Characters.Find(c => c.Id == characterId.Value) : null;

            await commandHandler.ExecuteAsync(new SetParagraphCharacterCommand(folderId, paragraph.Id, characterId));
            ParagraphCharacterStamp.Apply(paragraph.Items, characterId, character);

            // Clearing the stamp leaves every character item unattributed — count, never assume 0.
            nodeStatus.OnCharacterAttributed(folderId, paragraph.Id, CountUnattributed(paragraph.Items));

            InvalidateVoicePreview();
            NotifyStateChanged();
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

            // Folder-wide re-seed rather than per-paragraph patching: uniform for assign and clear, and
            // correct for selected paragraphs whose chapters were never expanded. Fires Changed once,
            // and does so before the stamp so it cannot land mid-walk.
            nodeStatus.Seed(folderId, await reader.GetNodeStatusSeedAsync(folderId));

            // Walk the loaded paragraphs testing membership, never the selection looking ids up — the
            // selection can dwarf what is in memory. Unloaded paragraphs need nothing: their chapter
            // reads the committed write when it expands.
            foreach (var p in Tree.AllParagraphs())
            {
                if (Selection.IsParagraphSelected(p.Id))
                    ParagraphCharacterStamp.Apply(p.Items, characterId, character);
            }

            InvalidateVoicePreview();
            NotifyStateChanged();

            snackbar.Add(
                name is null
                    ? $"Cleared speakers on {items} lines in {paras} paragraphs."
                    : $"Assigned {name} to {items} lines in {paras} paragraphs.",
                Severity.Success);
        }

        /// <summary>
        /// The roster entry behind a picked id, refreshing from the reader when the id is newer than
        /// the roster — the add-new path, where the character was created after this presenter loaded.
        /// Null for a clear.
        /// </summary>
        private async Task<Character?> ResolveCharacterAsync(ProjectFolderId folderId, Guid? characterId)
        {
            if (characterId is not { } id) return null;

            var character = Characters.Find(c => c.Id == id);
            if (character is not null) return character;

            Characters = await reader.GetCharactersAsync(folderId);
            return Characters.Find(c => c.Id == id);
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
            Characters = await reader.GetCharactersAsync(folderId);
            NotifyStateChanged();
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

        // ---------------------------------------------------------------
        // Selection mutators — delegate to ISelectionCoordinator
        // ---------------------------------------------------------------

        public async Task ToggleParagraphAsync(
            ProjectFolderId folderId, Guid paragraphId,
            Guid chapterId, Guid partId, Guid volumeId, bool on)
        {
            await selectionCoordinator.ToggleParagraphAsync(folderId, paragraphId, chapterId, partId, volumeId, on);
            NotifyStateChanged();
        }

        public async Task SetNodeAsync(
            ProjectFolderId folderId, BookNodeLevel level, Guid id, bool on, bool unprocessedOnly = false)
        {
            await selectionCoordinator.SetNodeAsync(folderId, level, id, on, unprocessedOnly);
            NotifyStateChanged();
        }

        public int SelectedParagraphCount => selectionCoordinator.SelectedParagraphCount;

        // ---------------------------------------------------------------
        // Audio item selection mutators — delegate to ISelectionCoordinator
        // ---------------------------------------------------------------

        public async Task ToggleAudioItemAsync(AudioItemRef item, bool on)
        {
            await selectionCoordinator.ToggleAudioItemAsync(item, on);
            NotifyStateChanged();
        }

        public async Task SetAudioNodeAsync(ProjectFolderId folderId, BookNodeLevel level, Guid nodeId, bool on, bool needsAudioOnly = false)
        {
            await selectionCoordinator.SetAudioNodeAsync(folderId, level, nodeId, on, needsAudioOnly, NarratorOnlyMode);
            NotifyStateChanged();
        }

        public int SelectedAudioItemCount => selectionCoordinator.SelectedAudioItemCount;

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
            var para = Tree.TryGetOwner(paragraphItemId);
            if (para is null) return;

            var hasAnyNeedsReview = para.Items.Any(i =>
                audioReviews.ReviewOf(folderId, i.Id)?.State == AudioReviewState.NeedsReview);
            nodeStatus.OnReviewChanged(folderId, para.Id, hasAnyNeedsReview);
        }

        public async Task AddSelectionToAudioQueueAsync()
        {
            await selectionCoordinator.AddSelectionToAudioQueueAsync();
            NotifyStateChanged();
        }

        public ProjectFolderId? CurrentFolder => _lastFolder;

        public async Task AddSelectionToCharacterQueueAsync()
        {
            await selectionCoordinator.AddSelectionToCharacterQueueAsync();
            NotifyStateChanged();
        }

        private async Task ExecuteAndReloadAsync(
            ProjectFolderId folderId,
            Func<Task<Result>> operation,
            bool resetTree)
        {
            IsBusy = true;
            Error = null;
            NotifyStateChanged();
            var result = await operation();
            Error = result.IsSuccess ? null : result.Error;
            if (result.IsSuccess)
                await (resetTree ? ResetAndLoadAsync(folderId) : LoadAsync(folderId));
            IsBusy = false;
            NotifyStateChanged();
        }

        private Task ExecuteCommandAndReloadAsync(ProjectFolderId folderId, BookCommand command) =>
            ExecuteAndReloadAsync(folderId, async () =>
            {
                await commandHandler.ExecuteAsync(command);
                return Result.Ok();
            }, resetTree: true);

        private void NotifyStateChanged() => StateChanged?.Invoke();

        /// <summary>The node-status counter unit: character items still without a stamp.</summary>
        private static int CountUnattributed(IEnumerable<ParagraphItem>? items) =>
            items?.Count(i => i.ItemType == Data.Enums.ParagraphItemType.Character && i.CharacterId is null) ?? 0;

        /// <summary>
        /// A paragraph's items were rewritten (attribution applied a segment list, or an item was
        /// stamped by hand). Segmentation can add and remove items, so the whole item list is
        /// reloaded rather than a single stamp patched.
        /// </summary>
        private async void OnParagraphItemsChanged(ParagraphItemsChanged e)
        {
            if (_lastFolder is not { } current || current.Value != e.FolderId.Value) return;

            var para = Tree.AllParagraphs().FirstOrDefault(p => p.Id == e.ParagraphId);
            if (para is null) return;

            var children = await reader.GetChildrenAsync(e.FolderId, BookNodeLevel.Chapter, para.ChapterId);
            var reloaded = children?.Paragraphs?.FirstOrDefault(p => p.Id == e.ParagraphId);
            if (reloaded is null) return;

            para.Items = reloaded.Items;

            // Attribution can create brand-new characters. Refresh the roster so they show up in
            // the chip menu without a navigate-away/back reload.
            var stamped = para.Items.Select(i => i.CharacterId).OfType<Guid>().ToList();
            if (stamped.Any(id => Characters.All(c => c.Id != id)))
                Characters = await reader.GetCharactersAsync(e.FolderId);

            nodeStatus.OnCharacterAttributed(e.FolderId, e.ParagraphId, CountUnattributed(para.Items));
            InvalidateVoicePreview();
            NotifyStateChanged();
        }

        private void OnAudioFileAssigned(ProjectFolderId folder, Guid paragraphItemId, string relativePath)
        {
            if (_lastFolder is not { } current || current.Value != folder.Value) return;

            var para = Tree.TryGetOwner(paragraphItemId);
            if (para is null) return;

            var item = para.Items.First(i => i.Id == paragraphItemId);
            item.AudioFileName = relativePath;
            nodeStatus.OnAudioAssigned(folder, item.ParagraphId);
        }

        /// <summary>
        /// A bulk write must never meet an in-flight attribution, so any armed bulk mode is turned
        /// off — not merely greyed out — the moment the character queue has work.
        /// </summary>
        private void DisarmBulkIfQueueBusy()
        {
            if (characterQueue.Snapshot().IsBusy && Selection is not null)
                Selection.BulkMode = false;
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
        }
    }
}
