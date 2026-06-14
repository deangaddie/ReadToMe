using System;
using System.Collections.Generic;
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

        public event Action? StateChanged;

        public async Task LoadAsync(ProjectFolderId folderId)
        {
            IsLoading = true;
            Tree = treeState.For(folderId);
            ConfirmReread = false;

            var project = await reader.GetProjectAsync(folderId);
            Filename = project?.Filename;
            HasContent = await reader.HasBookContentAsync(folderId);
            Volumes = HasContent ? await reader.GetVolumesAsync(folderId) : [];
            Characters = HasContent ? await reader.GetCharactersAsync(folderId) : new List<Character>();
            TotalParts = HasContent ? await reader.GetTotalPartCountAsync(folderId) : 0;
            TotalChapters = HasContent ? await reader.GetTotalChapterCountAsync(folderId) : 0;

            // Single volume is always auto-expanded; seed it if not already tracked.
            if (Volumes.Count == 1)
                Tree.ExpandedVolumeIds.Add(Volumes[0].Id);

            await Tree.RestoreExpandedAsync();

            IsLoading = false;
            NotifyStateChanged();
        }

        public async Task ResetAndLoadAsync(ProjectFolderId folderId)
        {
            Tree?.Reset();
            await LoadAsync(folderId);
        }

        public async Task ReadBookAsync(ProjectFolderId folderId)
        {
            IsBusy = true;
            Error = null;
            NotifyStateChanged();
            var result = await bookUseCases.ImportAsync(folderId);
            Error = result.IsSuccess ? null : result.Error;
            if (result.IsSuccess) await LoadAsync(folderId);
            IsBusy = false;
            NotifyStateChanged();
        }

        public async Task ConfirmRereadAsync(ProjectFolderId folderId)
        {
            IsBusy = true;
            Error = null;
            NotifyStateChanged();
            var result = await bookUseCases.ImportAsync(folderId, reread: true);
            Error = result.IsSuccess ? null : result.Error;
            if (result.IsSuccess) await ResetAndLoadAsync(folderId);
            IsBusy = false;
            NotifyStateChanged();
        }

        public async Task ManualRereadAsync(ProjectFolderId folderId)
        {
            var dialog = await dialogService.ShowAsync<Shared.ManualRereadDialog>("Manual Reread Book");
            var result = await dialog.Result;
            if (result?.Canceled != false) return;

            var options = result.Data as ManualReadOptions;
            if (options is null) return;

            IsBusy = true;
            Error = null;
            NotifyStateChanged();
            var importResult = await bookUseCases.ImportManuallyAsync(folderId, options);
            Error = importResult.IsSuccess ? null : importResult.Error;
            if (importResult.IsSuccess) await ResetAndLoadAsync(folderId);
            IsBusy = false;
            NotifyStateChanged();
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
            item.CharacterId = characterId;
            item.Character = characterId.HasValue
                ? Characters.Find(c => c.Id == characterId.Value)
                : null;
            NotifyStateChanged();
        }

        public async Task AddBookTitleAsync(ProjectFolderId folderId)
        {
            IsBusy = true;
            Error = null;
            NotifyStateChanged();
            await commandHandler.ExecuteAsync(new AddBookTitleCommand(folderId));
            await ResetAndLoadAsync(folderId);
            IsBusy = false;
            NotifyStateChanged();
        }

        public async Task AddVolumeTitlesAsync(ProjectFolderId folderId)
        {
            IsBusy = true;
            Error = null;
            NotifyStateChanged();
            await commandHandler.ExecuteAsync(new AddVolumeTitlesCommand(folderId));
            await ResetAndLoadAsync(folderId);
            IsBusy = false;
            NotifyStateChanged();
        }

        public async Task AddPartTitlesAsync(ProjectFolderId folderId)
        {
            IsBusy = true;
            Error = null;
            NotifyStateChanged();
            await commandHandler.ExecuteAsync(new AddPartTitlesCommand(folderId));
            await ResetAndLoadAsync(folderId);
            IsBusy = false;
            NotifyStateChanged();
        }

        public async Task AddChapterTitlesAsync(ProjectFolderId folderId)
        {
            IsBusy = true;
            Error = null;
            NotifyStateChanged();
            await commandHandler.ExecuteAsync(new AddChapterTitlesCommand(folderId));
            await ResetAndLoadAsync(folderId);
            IsBusy = false;
            NotifyStateChanged();
        }

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

        private void NotifyStateChanged() => StateChanged?.Invoke();
    }
}
