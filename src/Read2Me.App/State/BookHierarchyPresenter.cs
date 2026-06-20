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
        IDialogService dialogService,
        CharacterQueueService characterQueue) : IDisposable
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

        private HashSet<Guid> _selectableNodes = [];
        private IReadOnlyDictionary<Guid, int> _nodeCounts = new Dictionary<Guid, int>();

        public bool IsNodeSelectable(Guid nodeId) => _selectableNodes.Contains(nodeId);

        private ProjectFolderId? _lastFolder;
        private bool _queueSubscribed;

        public event Action? StateChanged;

        public async Task LoadAsync(ProjectFolderId folderId)
        {
            IsLoading = true;

            if (!_queueSubscribed)
            {
                characterQueue.Changed += OnQueueChanged;
                _queueSubscribed = true;
            }

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
            Selection.SetCounts(_nodeCounts);

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
            NotifyStateChanged();
        }

        public async Task SetParagraphCharacterAsync(ProjectFolderId folderId, Paragraph paragraph, Guid? characterId)
        {
            characterQueue.ClearOutcome(folderId, paragraph.Id);

            var character = characterId.HasValue ? Characters.Find(c => c.Id == characterId.Value) : null;

            await commandHandler.ExecuteAsync(new SetParagraphCharacterCommand(folderId, paragraph.Id, characterId));
            ParagraphCharacterStamp.Apply(paragraph.Items, characterId, character);
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

        public void Dispose()
        {
            if (_queueSubscribed)
            {
                characterQueue.Changed -= OnQueueChanged;
                _queueSubscribed = false;
            }
        }
    }
}
