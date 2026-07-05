using System;
using System.Linq;
using System.Threading.Tasks;
using MudBlazor;
using Read2Me.App.Services.Preflight;
using Read2Me.Core.Models;
using Read2Me.Services;
using Read2Me.Services.Audio;
using Read2Me.Services.Characters;

namespace Read2Me.App.State
{
    public sealed class BookSelectionCoordinator(
        IProjectReader reader,
        CharacterQueueService characterQueue,
        AudioQueueService audioQueue,
        ParagraphTtsSettingsService paragraphTtsSettings,
        ISnackbar snackbar,
        BookSelectionState selectionState,
        AudioItemSelectionState audioSelectionState,
        IAiPreflight preflight) : ISelectionCoordinator
    {
        private ProjectFolderId? _lastFolder;

        public void SetCurrentFolder(ProjectFolderId folderId) => _lastFolder = folderId;

        private FolderSelection Selection(ProjectFolderId folderId)
        {
            _lastFolder = folderId;
            return selectionState.For(folderId);
        }

        private AudioItemSelection AudioSelection(ProjectFolderId folderId)
        {
            _lastFolder = folderId;
            return audioSelectionState.For(folderId);
        }

        public Task ToggleParagraphAsync(
            ProjectFolderId folderId, Guid paragraphId,
            Guid chapterId, Guid partId, Guid volumeId, bool on)
        {
            var sel = Selection(folderId);
            if (on)
                sel.AddParagraph(paragraphId, new ParagraphSelection(volumeId, partId, chapterId));
            else
                sel.RemoveParagraph(paragraphId);
            return Task.CompletedTask;
        }

        public async Task SetNodeAsync(
            ProjectFolderId folderId, BookNodeLevel level, Guid id, bool on, bool unprocessedOnly = false)
        {
            var refs = await reader.GetCharacterParagraphsAsync(folderId, level, id, unprocessedOnly);
            var sel = Selection(folderId);
            if (on)
                sel.AddParagraphs(refs);
            else
                sel.RemoveParagraphs(refs.Select(r => r.ParagraphId));
        }

        public int SelectedParagraphCount =>
            _lastFolder is { } f ? selectionState.For(f).SelectedParagraphCount : 0;

        public async Task AddSelectionToCharacterQueueAsync()
        {
            if (_lastFolder is not { } folder) return;
            var sel = selectionState.For(folder);

            var selectedIds = sel.SelectedParagraphIds().ToList();
            if (selectedIds.Count == 0) return;

            if (!await preflight.EnsureReadyAsync(AiTaskKind.CharacterAttribution)) return;

            var ordered = await reader.GetOrderedParagraphsAsync(folder, selectedIds);
            var items = ordered.Select(p =>
            {
                var anc = sel.GetAncestry(p.ParagraphId);
                return new QueuedParagraph(folder, p.ParagraphId, p.Preview,
                    anc?.ChapterId ?? Guid.Empty, anc?.PartId ?? Guid.Empty, anc?.VolumeId ?? Guid.Empty);
            });

            characterQueue.Enqueue(items);
            sel.Clear();
        }

        public Task ToggleAudioItemAsync(AudioItemRef item, bool on)
        {
            if (_lastFolder is not { } f) return Task.CompletedTask;
            var sel = audioSelectionState.For(f);
            if (on) sel.AddItem(item); else sel.RemoveItem(item.ParagraphItemId);
            return Task.CompletedTask;
        }

        public async Task SetAudioNodeAsync(
            ProjectFolderId folderId, BookNodeLevel level, Guid nodeId, bool on,
            bool needsAudioOnly = false, bool narratorOnlyMode = false)
        {
            var refs = await reader.GetAudioItemRefsAsync(folderId, level, nodeId, needsAudioOnly, narratorOnlyMode);
            var sel = AudioSelection(folderId);
            if (on)
                sel.AddItems(refs);
            else
                sel.RemoveItems(refs.Select(r => r.ParagraphItemId));
        }

        public int SelectedAudioItemCount =>
            _lastFolder is { } f ? audioSelectionState.For(f).SelectedItemCount : 0;

        public async Task AddSelectionToAudioQueueAsync()
        {
            if (_lastFolder is not { } folder) return;
            var sel = audioSelectionState.For(folder);

            var selectedIds = sel.SelectedItems().Select(r => r.ParagraphItemId).ToList();
            if (selectedIds.Count == 0) return;

            var activeConfig = await paragraphTtsSettings.GetActiveConfigAsync();
            if (activeConfig is null)
            {
                snackbar.Add(
                    "No paragraph TTS service configured. Go to Paragraph TTS Settings to add one.",
                    Severity.Warning);
                return;
            }

            if (!await preflight.EnsureReadyAsync(AiTaskKind.AudioGeneration)) return;

            var items = await reader.GetOrderedAudioItemRefsAsync(folder, selectedIds);
            audioQueue.Enqueue(folder, items);
            sel.Clear();
        }
    }
}
