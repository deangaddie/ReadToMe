using System;
using System.Threading.Tasks;
using Read2Me.Core.Models;

namespace Read2Me.App.State
{
    public interface ISelectionCoordinator
    {
        void SetCurrentFolder(ProjectFolderId folderId);

        Task ToggleParagraphAsync(ProjectFolderId folderId, Guid paragraphId, Guid chapterId, Guid partId, Guid volumeId, bool on);
        Task SetNodeAsync(ProjectFolderId folderId, BookNodeLevel level, Guid id, bool on, bool unprocessedOnly = false);
        int SelectedParagraphCount { get; }
        Task AddSelectionToCharacterQueueAsync();

        Task ToggleAudioItemAsync(AudioItemRef item, bool on);
        Task SetAudioNodeAsync(ProjectFolderId folderId, BookNodeLevel level, Guid nodeId, bool on, bool needsAudioOnly = false, bool narratorOnlyMode = false);
        int SelectedAudioItemCount { get; }
        Task AddSelectionToAudioQueueAsync();
    }
}
