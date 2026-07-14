using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components;
using MudBlazor;
using Read2Me.App.Shared;
using Read2Me.App.Shared.Voices;
using Read2Me.App.State;
using Read2Me.Core.Models;
using Read2Me.Services;
using Read2Me.Services.Audio;
using VoiceEntity = Read2Me.Data.Entities.Voice;

namespace Read2Me.App.Pages
{
    /// <summary>
    /// The voice audio editor. A real page rather than a dialog: it is folder-scoped, linkable and
    /// back-navigable, and the master-detail layout needs the room.
    /// <para>
    /// The page holds no edit state of its own — <see cref="VoiceAudioEditorModel"/> owns all of it,
    /// so the interesting behaviour (stale renders, the Apply gate, the hiss hint) is testable without
    /// a renderer.
    /// </para>
    /// </summary>
    public partial class VoiceAudioEditorPage
    {
        [Parameter] public string FolderName { get; set; } = "";
        [Parameter] public Guid VoiceId { get; set; }

        [Inject] IProjectReader Reader { get; set; } = null!;
        [Inject] IVoicePreviewRenderer PreviewRenderer { get; set; } = null!;
        [Inject] IVoiceAudioEditor Editor { get; set; } = null!;
        [Inject] IVoiceOriginalStore Originals { get; set; } = null!;
        [Inject] CharacterPresenter Presenter { get; set; } = null!;
        [Inject] IDialogService DialogService { get; set; } = null!;
        [Inject] ISnackbar Snackbar { get; set; } = null!;
        [Inject] NavigationManager Nav { get; set; } = null!;

        VoiceEntity? _voice;
        VoiceAudioEditorModel? _model;
        string? _loadError;

        /// Cache-buster shared with the characters tab: Apply and Restore overwrite the live WAV in
        /// place, so without it the browser plays the pre-edit audio.
        int _originalToken;

        ProjectFolderId Folder => new(FolderName);

        VoiceAudioRef VoiceRef => new(Folder, _voice!.CharacterId, _voice.Id, _voice.AudioFileName!);

        /// <summary>
        /// The "before" player: the stored original when the voice has been edited, else the live WAV,
        /// which *is* the original until the first Apply. Both sit under the project folder, so the
        /// existing static /workspace mount serves them — no endpoint, no cache, no eviction.
        /// </summary>
        string OriginalUrl
        {
            get
            {
                var path = _model?.Edited == true
                    ? Originals.RelativePath(_voice!.CharacterId, _voice.Id)
                    : _voice!.AudioFileName!.Replace('\\', '/');
                return $"/workspace/{FolderName}/{path}?v={_originalToken}";
            }
        }

        protected override async Task OnParametersSetAsync()
        {
            _voice = await Reader.GetVoiceAsync(Folder, VoiceId);

            if (_voice is null)
            {
                _loadError = "That voice no longer exists.";
                return;
            }

            if (string.IsNullOrEmpty(_voice.AudioFileName))
            {
                _loadError = "This voice has no audio to edit.";
                return;
            }

            var edited = Originals.Exists(Folder, _voice.CharacterId, _voice.Id);
            _model = new VoiceAudioEditorModel(PreviewRenderer, Editor, edited);
        }

        async Task PreviewAsync() => await _model!.PreviewAsync(VoiceRef);

        async Task ApplyAsync()
        {
            if (!await _model!.ApplyAsync(VoiceRef))
            {
                if (_model.Error is not null)
                    Snackbar.Add(_model.Error, Severity.Error);
                return;
            }

            // The live WAV was overwritten under the same name, here and on the characters tab.
            _originalToken++;
            Presenter.BumpAudioToken(VoiceId);
            Snackbar.Add("Voice audio updated.", Severity.Success);
        }

        async Task RestoreAsync()
        {
            var confirmed = await DialogService.ConfirmAsync(
                "Restore original",
                "Restore this voice's original audio? The edit will be discarded.",
                "Restore");
            if (!confirmed) return;

            if (!await _model!.RestoreAsync(VoiceRef))
            {
                if (_model.Error is not null)
                    Snackbar.Add(_model.Error, Severity.Error);
                return;
            }

            _originalToken++;
            Presenter.BumpAudioToken(VoiceId);
            Snackbar.Add("Original audio restored.", Severity.Success);
        }

        void Back() => Nav.NavigateTo($"/project/{FolderName}");
    }
}
