using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using MudBlazor;
using Read2Me.App.Services.Preflight;
using Read2Me.App.State;
using Read2Me.AppData.Entities;
using Read2Me.Data.Entities;
using Read2Me.Core.Models;
using Read2Me.Data.Enums;
using Read2Me.Services;
using Read2Me.Services.Audio.VoiceDesign.Settings;

namespace Read2Me.App.Shared.Characters
{
    public partial class CharacterDetailPanel
    {
        [Parameter, EditorRequired] public Character Character { get; set; } = null!;
        [Parameter, EditorRequired] public IReadOnlyList<Read2Me.Core.Models.CharacterLine> Lines { get; set; } = [];
        [Parameter, EditorRequired] public IReadOnlyList<Character> AllCharacters { get; set; } = [];
        [Parameter, EditorRequired] public string FolderName { get; set; } = "";

        [Inject] internal CharacterPresenter Presenter { get; set; } = null!;
        [Inject] IDialogService DialogService { get; set; } = null!;
        [Inject] ISnackbar Snackbar { get; set; } = null!;
        [Inject] VoiceDesignSettingsService VoiceDesignSettingsService { get; set; } = null!;
        [Inject] ParagraphTtsSettingsService ParagraphTtsSettingsService { get; set; } = null!;
        [Inject] IAiPreflight Preflight { get; set; } = null!;
        [Inject] internal VoicePromptGenerationState VoicePromptState { get; set; } = null!;

        VoiceDesignServiceConfig? _activeVoiceDesignConfig;
        ParagraphTtsServiceConfig? _activeParagraphTtsConfig;

        protected override async Task OnInitializedAsync()
        {
            _activeVoiceDesignConfig = await VoiceDesignSettingsService.GetActiveConfigAsync();
            _activeParagraphTtsConfig = await ParagraphTtsSettingsService.GetActiveConfigAsync();
        }

        internal string? ActiveProviderDefaultsJson => _activeVoiceDesignConfig?.SettingsJson
            ?? JsonSerializer.Serialize(VoxCpm2VoiceDesignSettings.Recommended, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        internal string? ActiveTtsProviderDefaultsJson => _activeParagraphTtsConfig?.SettingsJson;
        internal ParagraphTtsServiceType ActiveTtsProviderType => _activeParagraphTtsConfig?.Type ?? ParagraphTtsServiceType.VoxCpm2;

        bool _addingAlias;
        string _newAlias = "";
        Guid? _regeneratingPrompt;
        Guid? _generatingAudio;
        Guid? _transcribing;
        Guid? _renamingVoice;
        string _renameValue = "";

        internal readonly VoiceDraftBuffer _drafts = new();
        readonly HashSet<Guid> _reloadingAudio = new();

        internal string AudioUrl(Voice v) =>
            $"/workspace/{FolderName}/{v.AudioFileName?.Replace('\\', '/')}?v={Presenter.AudioToken(v.Id)}";

        async Task CycleAudioPlayerAsync(Guid voiceId)
        {
            _reloadingAudio.Add(voiceId);
            StateHasChanged();
            await Task.Yield();
            _reloadingAudio.Remove(voiceId);
            StateHasChanged();
        }

        internal bool HasPromptDraft(Voice v) => _drafts.IsDirty(v.Id, VoiceDraftField.Prompt, v.DesignPrompt);
        internal bool HasTranscriptDraft(Voice v) => _drafts.IsDirty(v.Id, VoiceDraftField.Transcript, v.Transcript);
        internal bool HasDescriptionDraft(Voice v) => _drafts.IsDirty(v.Id, VoiceDraftField.Description, v.Description);

        internal string GetDescriptionDraft(Voice v) => _drafts.Current(v.Id, VoiceDraftField.Description, v.Description);
        internal string GetPromptDraft(Voice v) => _drafts.Current(v.Id, VoiceDraftField.Prompt, v.DesignPrompt);
        internal string GetTranscriptDraft(Voice v) => _drafts.Current(v.Id, VoiceDraftField.Transcript, v.Transcript);
        internal string GetOverrideDraft(Voice v) => _drafts.Current(v.Id, VoiceDraftField.Override, v.VoiceDesignSettingsOverrideJson);
        internal string GetTtsOverrideDraft(Voice v) => _drafts.Current(v.Id, VoiceDraftField.TtsOverride, v.TtsSettingsOverrideJson);

        async Task SaveOverrideAsync(Voice v)
        {
            var raw = _drafts.Current(v.Id, VoiceDraftField.Override, v.VoiceDesignSettingsOverrideJson);
            string? json = string.IsNullOrWhiteSpace(raw) ? null : raw;
            if (json is not null)
            {
                try { System.Text.Json.JsonDocument.Parse(json); }
                catch { Snackbar.Add("Override is not valid JSON.", Severity.Error); return; }
            }
            await Presenter.SetVoiceSettingsOverrideAsync(v.Id, json);
            _drafts.Clear(v.Id, VoiceDraftField.Override);
            Snackbar.Add("Settings override saved.", Severity.Success);
        }

        async Task SaveTtsOverrideAsync(Voice v)
        {
            var raw = _drafts.Current(v.Id, VoiceDraftField.TtsOverride, v.TtsSettingsOverrideJson);
            string? json = string.IsNullOrWhiteSpace(raw) ? null : raw;
            if (json is not null)
            {
                try { System.Text.Json.JsonDocument.Parse(json); }
                catch { Snackbar.Add("Override is not valid JSON.", Severity.Error); return; }
            }
            await Presenter.SetVoiceTtsSettingsOverrideAsync(v.Id, json);
            _drafts.Clear(v.Id, VoiceDraftField.TtsOverride);
            Snackbar.Add("TTS settings override saved.", Severity.Success);
        }

        // ── Alias ─────────────────────────────────────────────────────────────────

        async Task CommitAliasAsync()
        {
            var name = _newAlias.Trim();
            if (!string.IsNullOrEmpty(name))
                await Presenter.AddAliasAsync(Character.Id, name);
            CancelAlias();
        }

        async Task OnAliasKeyDownAsync(KeyboardEventArgs e)
        {
            if (e.Key == "Enter") await CommitAliasAsync();
            else if (e.Key == "Escape") CancelAlias();
        }

        void CancelAlias() { _addingAlias = false; _newAlias = ""; }

        async Task RemoveAliasAsync(Guid aliasId) => await Presenter.RemoveAliasAsync(aliasId);

        // ── Character rename ─────────────────────────────────────────────────────

        bool _renamingCharacter;
        string _renameCharacterValue = "";

        void BeginRenameCharacter() { _renamingCharacter = true; _renameCharacterValue = Character.Name; }
        void CancelRenameCharacter() { _renamingCharacter = false; _renameCharacterValue = ""; }

        async Task CommitRenameCharacterAsync()
        {
            var name = _renameCharacterValue.Trim();
            if (!string.IsNullOrEmpty(name) && name != Character.Name)
                await Presenter.RenameCharacterAsync(Character.Id, name);
            CancelRenameCharacter();
        }

        async Task OnRenameCharacterKeyDownAsync(KeyboardEventArgs e)
        {
            if (e.Key == "Enter") await CommitRenameCharacterAsync();
            else if (e.Key == "Escape") CancelRenameCharacter();
        }

        // ── Add voice ─────────────────────────────────────────────────────────────

        async Task AddVoiceAsync()
        {
            var parameters = new DialogParameters<AddVoiceDialog>
            {
                { d => d.Character, Character },
            };
            var dialog = await DialogService.ShowAsync<AddVoiceDialog>("Add Voice", parameters);
            var result = await dialog.Result;
            if (result?.Canceled != false) return;
            if (result.Data is not AddVoiceDialog.VoiceDialogResult r) return;

            await Presenter.AddVoiceAndGetIdAsync(Character.Id, r.Name, r.IsGenerated);
        }

        // ── Voice rename ──────────────────────────────────────────────────────────

        void BeginRename(Voice v) { _renamingVoice = v.Id; _renameValue = v.Name; }
        void CancelRename() { _renamingVoice = null; _renameValue = ""; }

        async Task CommitRenameAsync(Voice v)
        {
            var name = _renameValue.Trim();
            if (!string.IsNullOrEmpty(name) && name != v.Name)
                await Presenter.UpdateVoiceAsync(v.Id, name, v.Description);
            CancelRename();
        }

        async Task OnRenameKeyDownAsync(KeyboardEventArgs e, Voice v)
        {
            if (e.Key == "Enter") await CommitRenameAsync(v);
            else if (e.Key == "Escape") CancelRename();
        }

        // ── Voice source switch ───────────────────────────────────────────────────

        async Task SwitchVoiceSourceAsync(Voice voice, VoiceSource newSource)
        {
            if (voice.Source == newSource) return;
            await Presenter.SetVoiceSourceAsync(voice.Id, newSource == VoiceSource.Generated);
        }

        // ── Reference audio upload ────────────────────────────────────────────────

        async Task OnReplaceAudioAsync(InputFileChangeEventArgs e, Voice voice)
        {
            var ext = Path.GetExtension(e.File.Name).ToLowerInvariant();
            await using var stream = e.File.OpenReadStream(maxAllowedSize: 200 * 1024 * 1024);
            await Presenter.ReplaceVoiceAudioAsync(Character.Id, voice.Id, voice.Name, stream, ext);
            if (Presenter.Error != null)
                Snackbar.Add(Presenter.Error, Severity.Error);
            else
            {
                await CycleAudioPlayerAsync(voice.Id);
                Snackbar.Add("Voice audio normalised.", Severity.Success);
            }
        }

        // ── Description editing ───────────────────────────────────────────────────

        async Task SaveDescriptionAsync(Voice voice)
        {
            if (!HasDescriptionDraft(voice)) return;
            var raw = _drafts.Current(voice.Id, VoiceDraftField.Description, voice.Description);
            var description = string.IsNullOrWhiteSpace(raw) ? null : raw;
            await Presenter.UpdateVoiceAsync(voice.Id, voice.Name, description);
            _drafts.Clear(voice.Id, VoiceDraftField.Description);
        }

        // ── Prompt editing ────────────────────────────────────────────────────────

        async Task SavePromptAsync(Voice voice)
        {
            if (!HasPromptDraft(voice)) return;
            var prompt = _drafts.Current(voice.Id, VoiceDraftField.Prompt, voice.DesignPrompt);
            await Presenter.SetVoiceDesignPromptDirectAsync(voice.Id, prompt);
            _drafts.Clear(voice.Id, VoiceDraftField.Prompt);
        }

        // Extracted for testability: owns the 3-step AI regeneration sequence.
        // Does not call StateHasChanged — callers do that in the component context.
        internal async Task<bool> RegeneratePromptCoreAsync(Voice voice, Func<string, Task<DialogResult?>> showDialog)
        {
            var builtPrompt = await Presenter.BuildDesignPromptAsync(Character.Id);
            if (builtPrompt == null) return false;

            var result = await showDialog(builtPrompt);
            if (result?.Canceled != false || result.Data is not string userPrompt) return false;

            // Publish the live LLM stream to the status dock while the model generates,
            // so the per-voice regenerate is expandable just like the batch/attribution runs.
            VoicePromptState.Begin(Character.Name);
            string? designPrompt;
            try
            {
                designPrompt = await Presenter.GenerateDesignPromptWithTextAsync(userPrompt);
            }
            finally
            {
                VoicePromptState.End();
            }
            if (designPrompt != null)
                _drafts.Set(voice.Id, VoiceDraftField.Prompt, designPrompt);
            return true;
        }

        // Razor-bound wrapper that provides the real MudBlazor dialog and manages UI state.
        async Task RegeneratePromptAsync(Voice voice)
        {
            // Guard before the prompt-edit dialog so the user is not shown a flow that then dies.
            if (!await Preflight.EnsureReadyAsync(AiTaskKind.VoicePromptGeneration)) return;

            await RegeneratePromptCoreAsync(voice, async builtPrompt =>
            {
                var parameters = new DialogParameters<GenerateWithAiDialog>
                {
                    { d => d.Prompt, builtPrompt },
                };
                var dialog = await DialogService.ShowAsync<GenerateWithAiDialog>("Generate with AI", parameters);
                var result = await dialog.Result;
                // User confirmed — show the per-voice spinner while the core generates.
                if (result?.Canceled == false && result.Data is string)
                {
                    _regeneratingPrompt = voice.Id;
                    StateHasChanged();
                }
                return result;
            });

            _regeneratingPrompt = null;
            StateHasChanged();
        }

        async Task GenerateAudioAsync(Voice voice)
        {
            var prompt = _drafts.Current(voice.Id, VoiceDraftField.Prompt, voice.DesignPrompt);
            if (string.IsNullOrWhiteSpace(prompt)) return;
            if (!await Preflight.EnsureReadyAsync(AiTaskKind.VoiceDesignAudio)) return;
            _generatingAudio = voice.Id;
            StateHasChanged();
            await Presenter.GenerateVoiceAudioAsync(Character.Id, voice.Id, voice.Name, prompt);
            _generatingAudio = null;
            if (Presenter.Error != null)
                Snackbar.Add(Presenter.Error, Severity.Error);
            else
            {
                _drafts.Clear(voice.Id, VoiceDraftField.Prompt);
                await CycleAudioPlayerAsync(voice.Id);
            }
        }

        // ── Transcript ────────────────────────────────────────────────────────────

        async Task SaveTranscriptAsync(Voice voice)
        {
            if (!HasTranscriptDraft(voice)) return;
            var t = _drafts.Current(voice.Id, VoiceDraftField.Transcript, voice.Transcript);
            await Presenter.SetVoiceTranscriptDirectAsync(voice.Id, t);
            _drafts.Clear(voice.Id, VoiceDraftField.Transcript);
        }

        async Task TranscribeAsync(Voice voice)
        {
            if (voice.AudioFileName == null) return;
            if (!await Preflight.EnsureReadyAsync(AiTaskKind.Transcription)) return;

            _transcribing = voice.Id;
            StateHasChanged();
            var fileName = Path.GetFileName(voice.AudioFileName);
            using var stream = Presenter.OpenVoiceAudioStream(voice);
            if (stream == null)
            {
                _transcribing = null;
                Snackbar.Add("Could not open the voice audio file.", Severity.Error);
                return;
            }

            await Presenter.TranscribeVoiceAsync(voice.Id, stream, fileName);
            _transcribing = null;
            _drafts.Clear(voice.Id, VoiceDraftField.Transcript);

            if (Presenter.Error != null)
                Snackbar.Add(Presenter.Error, Severity.Error);
            else
                Snackbar.Add("Transcript generated.", Severity.Success);
        }

        // ── Delete ────────────────────────────────────────────────────────────────

        async Task ConfirmDeleteVoiceAsync(Voice voice)
        {
            var parameters = new DialogParameters<ConfirmDeleteDialog>
            {
                { d => d.ItemType, "Voice" },
                { d => d.ItemName, voice.Name },
                { d => d.HasChildren, false }
            };
            var dialog = await DialogService.ShowAsync<ConfirmDeleteDialog>("Delete Voice", parameters);
            var result = await dialog.Result;
            if (result?.Canceled != false) return;
            await Presenter.DeleteVoiceAsync(voice.Id);
        }

        async Task OpenMergeAsync()
        {
            var others = System.Linq.Enumerable.ToList(System.Linq.Enumerable.Where(AllCharacters, c => c.Id != Character.Id && !c.IsNarrator));
            if (others.Count == 0) return;

            var parameters = new DialogParameters<MergeCharacterDialog>
            {
                { d => d.MergedCharacter, Character },
                { d => d.OtherCharacters, others }
            };
            var dialog = await DialogService.ShowAsync<MergeCharacterDialog>("Merge Character", parameters);
            var result = await dialog.Result;
            if (result?.Canceled != false) return;

            if (result.Data is MergeCharacterDialog.MergeResult m)
                await Presenter.MergeAsync(m.SurvivorId, Character.Id, m.AddNameAsAlias);
        }

        async Task ConfirmDeleteAsync()
        {
            var parameters = new DialogParameters<ConfirmDeleteDialog>
            {
                { d => d.ItemType, "Character" },
                { d => d.ItemName, Character.Name },
                { d => d.HasChildren, false }
            };
            var dialog = await DialogService.ShowAsync<ConfirmDeleteDialog>("Delete Character", parameters);
            var result = await dialog.Result;
            if (result?.Canceled != false) return;

            await Presenter.DeleteCharacterAsync(Character.Id);
        }

        // ── Voice Rules ───────────────────────────────────────────────────────────

        internal static string RuleDescription(Read2Me.Services.VoiceRuleRow r)
        {
            if (r.IsDefault)
                return $"Default → {r.VoiceName}";

            string NodeLabel(VoiceAnchorLevel? level, string? display, bool dangling)
            {
                if (dangling || display is null) return "(missing node)";
                var prefix = level switch
                {
                    VoiceAnchorLevel.Volume       => "Volume ",
                    VoiceAnchorLevel.Part         => "Part ",
                    VoiceAnchorLevel.Chapter      => "Chapter ",
                    VoiceAnchorLevel.Paragraph    => "Paragraph ",
                    VoiceAnchorLevel.ParagraphItem => "",
                    _ => ""
                };
                return prefix + display;
            }

            var fromLabel = NodeLabel(r.FromLevel, r.FromDisplayName, r.FromDangling);
            var toLabel   = NodeLabel(r.ToLevel,   r.ToDisplayName,   r.ToDangling);

            bool fromHereOn = r.ToLevel is null && !r.ToDangling;
            bool singleNode = r.FromLevel == r.ToLevel && r.FromNodeId == r.ToNodeId;

            if (fromHereOn)
                return $"From {fromLabel} onward → {r.VoiceName}";
            if (singleNode)
                return $"{fromLabel} → {r.VoiceName}";

            return $"{fromLabel} to {toLabel} → {r.VoiceName}";
        }

        async Task OpenAddRuleDialogAsync()
        {
            var parameters = new DialogParameters<AddVoiceRuleDialog>
            {
                { d => d.Character, Character },
                { d => d.Voices, Presenter.Voices },
            };
            var dialog = await DialogService.ShowAsync<AddVoiceRuleDialog>("Add Voice Rule", parameters);
            var result = await dialog.Result;
            if (result?.Canceled != false) return;
            if (result.Data is not AddVoiceRuleDialog.RuleDialogResult r) return;

            await Presenter.CreateVoiceRuleAsync(
                Character.Id, r.VoiceId,
                r.FromLevel, r.FromNodeId,
                r.ToLevel, r.ToNodeId);
        }
    }
}
