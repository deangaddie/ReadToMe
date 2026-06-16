using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MudBlazor;
using Read2Me.Core.Models;
using Read2Me.Data.Entities;
using Read2Me.Services;
using Read2Me.Services.Characters;
using Read2Me.Services.UseCases;

namespace Read2Me.App.State
{
    public class BookHierarchyPresenter(
        IProjectReader reader,
        IBookCommandHandler commandHandler,
        BookUseCases bookUseCases,
        BookTreeState treeState,
        BookSelectionState selectionState,
        SelectionCoordinator selectionCoordinator,
        IDialogService dialogService,
        CharacterQueueService characterQueue)
    {
        public bool IsLoading { get; private set; }
        public bool HasContent { get; private set; }
        public bool IsBusy { get; private set; }
        public bool SplitView { get; set; }
        public bool ConfirmReread { get; private set; }
        public string? Filename { get; private set; }
        public string? Error { get; private set; }
        public IReadOnlyList<Volume> Volumes { get; private set; } = [];
        public List<Character> Characters { get; private set; } = [];
        public int TotalParts { get; private set; }
        public int TotalChapters { get; private set; }
        public PerFolderState Tree { get; private set; } = null!;
        public FolderSelection Selection { get; private set; } = null!;

        // Volume/part/chapter ids that contain at least one character paragraph.
        // Nodes absent here are not selectable (no checkbox shown).
        private HashSet<Guid> _selectableNodes = [];
        private IReadOnlyDictionary<Guid, int> _nodeCounts = new Dictionary<Guid, int>();

        public bool IsNodeSelectable(Guid nodeId) => _selectableNodes.Contains(nodeId);

        private ProjectFolderId? _lastFolder;

        public event Action? StateChanged;

        public async Task LoadAsync(ProjectFolderId folderId)
        {
            IsLoading = true;

            // Clear selection when switching to a different project.
            if (_lastFolder.HasValue && _lastFolder.Value.Value != folderId.Value)
                selectionState.Reset(_lastFolder.Value);

            _lastFolder = folderId;
            Tree = treeState.For(folderId);
            Tree.Changed -= NotifyStateChanged;
            Tree.Changed += NotifyStateChanged;
            Selection = selectionState.For(folderId);
            ConfirmReread = false;

            var overview = await reader.GetBookOverviewAsync(folderId);
            Filename = overview.Filename;
            HasContent = overview.HasContent;
            Volumes = overview.Volumes;
            Characters = overview.Characters.ToList();
            TotalParts = overview.TotalParts;
            TotalChapters = overview.TotalChapters;
            _selectableNodes = overview.SelectableNodeIds;
            _nodeCounts = overview.NodeCharacterParagraphCounts;
            selectionCoordinator.SetNodeCounts(_nodeCounts);

            // Single volume is always auto-expanded; seed it if not already tracked.
            if (Volumes.Count == 1)
                Tree.ExpandedVolumeIds.Add(Volumes[0].Id);

            await Tree.RestoreExpandedAsync();

            IsLoading = false;
            NotifyStateChanged();
        }

        public async Task ResetAndLoadAsync(ProjectFolderId folderId)
        {
            selectionState.Reset(folderId);
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

        // Split levels mirror the new-parent entity that a split creates.
        public enum SplitLevel { Volume, Part, Chapter, Paragraph }

        // Execute a split, then reload. If the panel being split was expanded,
        // both the original parent and the newly created parent are expanded.
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
            NotifyStateChanged();
        }

        public async Task SetParagraphCharacterAsync(ProjectFolderId folderId, Paragraph paragraph, Guid? characterId)
        {
            characterQueue.ClearOutcome(folderId, paragraph.Id);

            var character = characterId.HasValue ? Characters.Find(c => c.Id == characterId.Value) : null;

            var charItems = paragraph.Items
                .Where(i => i.ItemType == Data.Enums.ParagraphItemType.Character)
                .ToList();

            if (characterId.HasValue)
            {
                await commandHandler.ExecuteAsync(new SetParagraphCharacterCommand(folderId, paragraph.Id, characterId.Value));
            }
            else
            {
                foreach (var item in charItems)
                    await commandHandler.ExecuteAsync(new SetItemCharacterCommand(folderId, item.Id, null));
            }

            foreach (var item in charItems)
            {
                item.CharacterId = characterId;
                item.Character = character;
            }

            NotifyStateChanged();
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

        public async Task ToggleParagraphAsync(
            ProjectFolderId folderId, Guid paragraphId,
            Guid chapterId, Guid partId, Guid volumeId, bool on)
        {
            await selectionCoordinator.ToggleParagraphAsync(
                Selection, folderId, paragraphId, chapterId, partId, volumeId, on);
            NotifyStateChanged();
        }

        public async Task SetNodeAsync(
            ProjectFolderId folderId, SelectionNodeKind kind, Guid id, bool on, bool unprocessedOnly = false)
        {
            await selectionCoordinator.SetNodeAsync(Selection, folderId, kind, id, on, unprocessedOnly);
            NotifyStateChanged();
        }

        public int SelectedParagraphCount => Selection?.SelectedParagraphCount ?? 0;

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
    }
}
