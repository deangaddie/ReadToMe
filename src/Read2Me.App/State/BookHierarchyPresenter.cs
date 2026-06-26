using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MudBlazor;
using Read2Me.Core.Models;
using Read2Me.Data.Entities;
using Read2Me.Services;
using Read2Me.Services.Audio;
using Read2Me.Services.Characters;
using Read2Me.Services.NodeStatus;
using Read2Me.Services.UseCases;

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
        ParagraphTtsSettingsService paragraphTtsSettings,
        CharacterQueueService characterQueue,
        AudioQueueService audioQueue,
        AudioReviewService audioReviews,
        NodeStatusService nodeStatus) : IDisposable
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

        private HashSet<Guid> _selectableNodes = [];
        private IReadOnlyDictionary<Guid, int> _nodeCounts = new Dictionary<Guid, int>();
        private IReadOnlyDictionary<Guid, int> _audioNodeCounts = new Dictionary<Guid, int>();

        public bool NarratorOnlyMode { get; private set; }

        // Voice preview cache for SplitAudio view: itemId → resolved voice name (null = no voice)
        private readonly Dictionary<Guid, string?> _voicePreviewCache = new();
        private readonly HashSet<Guid> _voicePreviewLoaded = new(); // item ids already fetched

        public string? ResolvedVoiceName(Guid itemId) =>
            _voicePreviewCache.TryGetValue(itemId, out var name) ? name : null;

        public async Task EnsureVoicePreviewAsync(ProjectFolderId folderId, IEnumerable<Guid> itemIds)
        {
            var missing = itemIds.Where(id => !_voicePreviewLoaded.Contains(id)).ToList();
            if (missing.Count == 0) return;
            foreach (var id in missing) _voicePreviewLoaded.Add(id);
            var resolved = await reader.GetResolvedVoiceNamesAsync(folderId, missing, NarratorOnlyMode);
            foreach (var (id, name) in resolved)
                _voicePreviewCache[id] = name;
        }

        public bool IsNodeSelectable(Guid nodeId) => _selectableNodes.Contains(nodeId);
        public bool IsNodeAudioSelectable(Guid nodeId) => _audioNodeCounts.ContainsKey(nodeId) && _audioNodeCounts[nodeId] > 0;

        private ProjectFolderId? _lastFolder;
        private bool _queueSubscribed;
        private bool _audioQueueSubscribed;

        public event Action? StateChanged;

        public async Task LoadAsync(ProjectFolderId folderId)
        {
            IsLoading = true;

            if (!_queueSubscribed)
            {
                characterQueue.Changed += OnQueueChanged;
                _queueSubscribed = true;
            }

            if (!_audioQueueSubscribed)
            {
                audioQueue.AudioFileAssigned += OnAudioFileAssigned;
                _audioQueueSubscribed = true;
            }

            if (_lastFolder.HasValue && _lastFolder.Value.Value != folderId.Value)
            {
                selectionState.Reset(_lastFolder.Value);
                audioSelectionState.Reset(_lastFolder.Value);
            }

            _lastFolder = folderId;
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
            _voicePreviewCache.Clear();
            _voicePreviewLoaded.Clear();
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

        public async Task SetItemCharacterAsync(ProjectFolderId folderId, ParagraphItem item, Guid? characterId)
        {
            await commandHandler.ExecuteAsync(new SetItemCharacterCommand(folderId, item.Id, characterId));
            characterQueue.ClearOutcome(folderId, item.ParagraphId);

            var character = characterId.HasValue ? Characters.Find(c => c.Id == characterId.Value) : null;
            if (characterId.HasValue && character is null)
            {
                Characters = await reader.GetCharactersAsync(folderId);
                character = Characters.Find(c => c.Id == characterId.Value);
            }

            item.CharacterId = characterId;
            item.Character = character;

            // Recompute remaining unattributed Character items now that the item is stamped.
            var remainingUnattributed = item.Paragraph?.Items
                .Count(i => i.ItemType == Data.Enums.ParagraphItemType.Character && i.CharacterId is null) ?? 0;
            nodeStatus.OnCharacterAttributed(folderId, item.ParagraphId, remainingUnattributed);

            NotifyStateChanged();
        }

        public async Task SetParagraphCharacterAsync(ProjectFolderId folderId, Paragraph paragraph, Guid? characterId)
        {
            characterQueue.ClearOutcome(folderId, paragraph.Id);

            var character = characterId.HasValue ? Characters.Find(c => c.Id == characterId.Value) : null;

            await commandHandler.ExecuteAsync(new SetParagraphCharacterCommand(folderId, paragraph.Id, characterId));
            ParagraphCharacterStamp.Apply(paragraph.Items, characterId, character);

            nodeStatus.OnCharacterAttributed(folderId, paragraph.Id, remainingUnattributed: 0);

            NotifyStateChanged();
        }

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
        // Selection mutators
        // ---------------------------------------------------------------

        public Task ToggleParagraphAsync(
            ProjectFolderId folderId, Guid paragraphId,
            Guid chapterId, Guid partId, Guid volumeId, bool on)
        {
            if (on)
                Selection.AddParagraph(paragraphId, new ParagraphSelection(volumeId, partId, chapterId));
            else
                Selection.RemoveParagraph(paragraphId);
            NotifyStateChanged();
            return Task.CompletedTask;
        }

        public async Task SetNodeAsync(
            ProjectFolderId folderId, BookNodeLevel level, Guid id, bool on, bool unprocessedOnly = false)
        {
            var refs = await reader.GetCharacterParagraphsAsync(folderId, level, id, unprocessedOnly);
            if (on)
                Selection.AddParagraphs(refs);
            else
                Selection.RemoveParagraphs(refs.Select(r => r.ParagraphId));
            NotifyStateChanged();
        }

        public int SelectedParagraphCount => Selection?.SelectedParagraphCount ?? 0;

        // ---------------------------------------------------------------
        // Audio item selection mutators
        // ---------------------------------------------------------------

        public Task ToggleAudioItemAsync(AudioItemRef item, bool on)
        {
            if (on)
                AudioSelection.AddItem(item);
            else
                AudioSelection.RemoveItem(item.ParagraphItemId);
            NotifyStateChanged();
            return Task.CompletedTask;
        }

        public async Task SetAudioNodeAsync(ProjectFolderId folderId, BookNodeLevel level, Guid nodeId, bool on, bool needsAudioOnly = false)
        {
            var refs = await reader.GetAudioItemRefsAsync(folderId, level, nodeId, needsAudioOnly, narratorOnlyMode: NarratorOnlyMode);
            if (on)
                AudioSelection.AddItems(refs);
            else
                AudioSelection.RemoveItems(refs.Select(r => r.ParagraphItemId));
            NotifyStateChanged();
        }

        public int SelectedAudioItemCount => AudioSelection?.SelectedItemCount ?? 0;

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

        public async Task AddSelectionToAudioQueue()
        {
            if (_lastFolder is not { } folder || AudioSelection is null) return;

            var selectedIds = AudioSelection.SelectedItems().Select(r => r.ParagraphItemId).ToList();
            if (selectedIds.Count == 0) return;

            var activeConfig = await paragraphTtsSettings.GetActiveConfigAsync();
            if (activeConfig is null)
            {
                snackbar.Add(
                    "No paragraph TTS service configured. Go to Paragraph TTS Settings to add one.",
                    Severity.Warning);
                return;
            }

            var items = await reader.GetOrderedAudioItemRefsAsync(folder, selectedIds);
            audioQueue.Enqueue(folder, items);
            AudioSelection.Clear();
            NotifyStateChanged();
        }

        public ProjectFolderId? CurrentFolder => _lastFolder;

        public async Task AddSelectionToCharacterQueue()
        {
            if (_lastFolder is not { } folder || Selection is null) return;

            var selectedIds = Selection.SelectedParagraphIds().ToList();
            if (selectedIds.Count == 0) return;

            var ordered = await reader.GetOrderedParagraphsAsync(folder, selectedIds);
            var items = ordered.Select(p =>
            {
                var anc = Selection.GetAncestry(p.ParagraphId);
                return new QueuedParagraph(folder, p.ParagraphId, p.Preview,
                    anc?.ChapterId ?? Guid.Empty, anc?.PartId ?? Guid.Empty, anc?.VolumeId ?? Guid.Empty);
            });

            characterQueue.Enqueue(items);
            Selection.Clear();
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

        private void OnQueueChanged()
        {
            // ParagraphRow components subscribe to CharacterQueueService.Changed directly
            // and re-render per-paragraph using Queue.ResolvedOf as a display overlay.
            // No tree-wide mutation needed here.
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

        public void Dispose()
        {
            if (_queueSubscribed)
            {
                characterQueue.Changed -= OnQueueChanged;
                _queueSubscribed = false;
            }

            if (_audioQueueSubscribed)
            {
                audioQueue.AudioFileAssigned -= OnAudioFileAssigned;
                _audioQueueSubscribed = false;
            }
        }
    }
}
