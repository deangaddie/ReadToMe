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
        CharacterQueueService characterQueue,
        AudioQueueService audioQueue,
        AudioReviewService audioReviews,
        NodeStatusService nodeStatus,
        IVoiceResolver voiceResolver,
        ISelectionCoordinator selectionCoordinator) : IDisposable
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
        private bool _characterAssignedSubscribed;

        public event Action? StateChanged;

        public async Task LoadAsync(ProjectFolderId folderId)
        {
            IsLoading = true;

            if (!_audioQueueSubscribed)
            {
                audioQueue.AudioFileAssigned += OnAudioFileAssigned;
                _audioQueueSubscribed = true;
            }

            if (!_characterAssignedSubscribed)
            {
                characterQueue.CharacterAssigned += OnCharacterAssigned;
                _characterAssignedSubscribed = true;
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

            InvalidateVoicePreview();
            NotifyStateChanged();
        }

        public async Task SetParagraphCharacterAsync(ProjectFolderId folderId, Paragraph paragraph, Guid? characterId)
        {
            characterQueue.ClearOutcome(folderId, paragraph.Id);

            var character = characterId.HasValue ? Characters.Find(c => c.Id == characterId.Value) : null;

            await commandHandler.ExecuteAsync(new SetParagraphCharacterCommand(folderId, paragraph.Id, characterId));
            ParagraphCharacterStamp.Apply(paragraph.Items, characterId, character);

            nodeStatus.OnCharacterAttributed(folderId, paragraph.Id, remainingUnattributed: 0);

            InvalidateVoicePreview();
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

        private void OnCharacterAssigned(ProjectFolderId folder, Guid paragraphId, ResolvedCharacter resolved)
        {
            if (_lastFolder is not { } current || current.Value != folder.Value) return;

            var para = Tree.AllParagraphs().FirstOrDefault(p => p.Id == paragraphId);
            if (para is null) return;

            nodeStatus.OnCharacterAttributed(folder, paragraphId, remainingUnattributed: 0);
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

        public void Dispose()
        {
            if (_audioQueueSubscribed)
            {
                audioQueue.AudioFileAssigned -= OnAudioFileAssigned;
                _audioQueueSubscribed = false;
            }

            if (_characterAssignedSubscribed)
            {
                characterQueue.CharacterAssigned -= OnCharacterAssigned;
                _characterAssignedSubscribed = false;
            }
        }
    }
}
