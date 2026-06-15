using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MudBlazor;
using Read2Me.Core.Models;
using Read2Me.Data.Entities;
using Read2Me.Services;
using Read2Me.Services.UseCases;

namespace Read2Me.App.State
{
    public class BookHierarchyPresenter(
        IProjectReader reader,
        IBookCommandHandler commandHandler,
        BookUseCases bookUseCases,
        BookTreeState treeState,
        BookSelectionState selectionState,
        IDialogService dialogService)
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
            Selection = selectionState.For(folderId);
            ConfirmReread = false;

            var project = await reader.GetProjectAsync(folderId);
            Filename = project?.Filename;
            HasContent = await reader.HasBookContentAsync(folderId);
            Volumes = HasContent ? await reader.GetVolumesAsync(folderId) : [];
            Characters = HasContent ? await reader.GetCharactersAsync(folderId) : new List<Character>();
            TotalParts = HasContent ? await reader.GetTotalPartCountAsync(folderId) : 0;
            TotalChapters = HasContent ? await reader.GetTotalChapterCountAsync(folderId) : 0;
            _selectableNodes = HasContent
                ? await reader.GetNodesWithCharacterParagraphsAsync(folderId)
                : [];

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
            if (on)
            {
                Selection.AddParagraph(paragraphId, new ParagraphSelection(volumeId, partId, chapterId));
                await WalkUpAsync(folderId, chapterId, partId, volumeId);
            }
            else
            {
                Selection.RemoveParagraph(paragraphId);
                Selection.RemoveNode(chapterId);
                Selection.RemoveNode(partId);
                Selection.RemoveNode(volumeId);
            }
            NotifyStateChanged();
        }

        public async Task SetNodeAsync(
            ProjectFolderId folderId, SelectionNodeKind kind, Guid id, bool on, bool unprocessedOnly = false)
        {
            List<CharacterParagraphRef> refs = kind switch
            {
                SelectionNodeKind.Volume => await GetUnprocessedOrAll(folderId, id, kind, unprocessedOnly),
                SelectionNodeKind.Part => await GetUnprocessedOrAll(folderId, id, kind, unprocessedOnly),
                _ => await GetUnprocessedOrAll(folderId, id, kind, unprocessedOnly),
            };

            if (on)
            {
                Selection.AddParagraphs(refs);
                Selection.AddNode(id);
                // Mark every descendant node fully selected so child checkboxes
                // render checked (not indeterminate) when a parent is selected.
                MarkDescendantNodesComplete(kind, id, refs);
                // Walk up from the selected node.
                await WalkUpFromNodeAsync(folderId, kind, id, refs);
            }
            else
            {
                Selection.RemoveParagraphs(refs.Select(r => r.ParagraphId));
                Selection.RemoveNode(id);
                // Deselecting clears descendant completeness marks added on select.
                RemoveDescendantNodes(kind, refs);
                // Deselecting removes all ancestor completeness marks.
                if (refs.Count > 0)
                {
                    var r = refs[0];
                    if (kind == SelectionNodeKind.Chapter)
                    {
                        Selection.RemoveNode(r.PartId);
                        Selection.RemoveNode(r.VolumeId);
                    }
                    else if (kind == SelectionNodeKind.Part)
                    {
                        Selection.RemoveNode(r.VolumeId);
                    }
                }
            }
            NotifyStateChanged();
        }

        public int SelectedParagraphCount => Selection?.SelectedParagraphCount ?? 0;

        // ---------------------------------------------------------------
        // Private helpers
        // ---------------------------------------------------------------

        private Task<List<CharacterParagraphRef>> GetUnprocessedOrAll(
            ProjectFolderId folderId, Guid id, SelectionNodeKind kind, bool unprocessedOnly)
        {
            // Today unprocessedOnly == all; filtering wired in later.
            return kind switch
            {
                SelectionNodeKind.Volume => reader.GetVolumeCharacterParagraphsAsync(folderId, id),
                SelectionNodeKind.Part => reader.GetPartCharacterParagraphsAsync(folderId, id),
                _ => reader.GetChapterCharacterParagraphsAsync(folderId, id),
            };
        }

        private async Task WalkUpAsync(ProjectFolderId folderId, Guid chapterId, Guid partId, Guid volumeId)
        {
            var chCount = await reader.GetChapterCharacterParagraphCountAsync(folderId, chapterId);
            var chSelected = Selection.SelectedCountUnder(chapterId, SelectionNodeKind.Chapter);
            if (chSelected >= chCount)
            {
                Selection.AddNode(chapterId);
                var ptCount = await reader.GetPartCharacterParagraphCountAsync(folderId, partId);
                var ptSelected = Selection.SelectedCountUnder(partId, SelectionNodeKind.Part);
                if (ptSelected >= ptCount)
                {
                    Selection.AddNode(partId);
                    var volCount = await reader.GetVolumeCharacterParagraphCountAsync(folderId, volumeId);
                    var volSelected = Selection.SelectedCountUnder(volumeId, SelectionNodeKind.Volume);
                    if (volSelected >= volCount)
                        Selection.AddNode(volumeId);
                }
            }
        }

        // Selecting a node selects every paragraph beneath it. The descendant
        // chapters/parts are therefore fully selected too — mark them so their
        // checkboxes show Checked instead of Indeterminate.
        private void MarkDescendantNodesComplete(
            SelectionNodeKind kind, Guid id, List<CharacterParagraphRef> refs)
        {
            if (kind == SelectionNodeKind.Volume)
            {
                foreach (var partId in refs.Select(r => r.PartId).Distinct())
                    Selection.AddNode(partId);
                foreach (var chapterId in refs.Select(r => r.ChapterId).Distinct())
                    Selection.AddNode(chapterId);
            }
            else if (kind == SelectionNodeKind.Part)
            {
                foreach (var chapterId in refs.Select(r => r.ChapterId).Distinct())
                    Selection.AddNode(chapterId);
            }
        }

        // Mirror of MarkDescendantNodesComplete: clear descendant node marks on deselect.
        private void RemoveDescendantNodes(SelectionNodeKind kind, List<CharacterParagraphRef> refs)
        {
            if (kind == SelectionNodeKind.Volume)
            {
                foreach (var partId in refs.Select(r => r.PartId).Distinct())
                    Selection.RemoveNode(partId);
                foreach (var chapterId in refs.Select(r => r.ChapterId).Distinct())
                    Selection.RemoveNode(chapterId);
            }
            else if (kind == SelectionNodeKind.Part)
            {
                foreach (var chapterId in refs.Select(r => r.ChapterId).Distinct())
                    Selection.RemoveNode(chapterId);
            }
        }

        private async Task WalkUpFromNodeAsync(
            ProjectFolderId folderId, SelectionNodeKind kind, Guid id,
            List<CharacterParagraphRef> refs)
        {
            if (refs.Count == 0) return;

            if (kind == SelectionNodeKind.Chapter)
            {
                var r = refs[0];
                await WalkUpAsync(folderId, id, r.PartId, r.VolumeId);
            }
            else if (kind == SelectionNodeKind.Part)
            {
                var r = refs[0];
                var volCount = await reader.GetVolumeCharacterParagraphCountAsync(folderId, r.VolumeId);
                var volSelected = Selection.SelectedCountUnder(r.VolumeId, SelectionNodeKind.Volume);
                if (volSelected >= volCount)
                    Selection.AddNode(r.VolumeId);
            }
            // Volume is top — no further walk.
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
