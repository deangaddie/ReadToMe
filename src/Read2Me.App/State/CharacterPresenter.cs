using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Read2Me.App.Services;
using Read2Me.Core.Models;
using Read2Me.Data;
using Read2Me.Data.Entities;
using Read2Me.Services;
using Read2Me.Services.Audio;
using Read2Me.Services.Characters;
using Read2Me.Services.Mutations;
using VoiceEntity = Read2Me.Data.Entities.Voice;

namespace Read2Me.App.State
{
    /// <summary>
    /// The Characters tab's producer of Book mutations (ADR 0007). Every roster, Voice and Voice
    /// Rule gesture on this page commits through <see cref="BookMutations"/>, so an open Book View —
    /// in this circuit or another — converges on it from the receipt.
    /// <para>
    /// The page keeps reads of its own rather than rendering a <c>BookViewSnapshot</c>: what it
    /// shows is one character's lines, voices and rules, none of which a Book View snapshot carries.
    /// So a gesture here still ends in <see cref="LoadAsync"/>, its own authoritative reread, and
    /// the tree converges separately.
    /// </para>
    /// <para>
    /// A refused mutation becomes <see cref="Error"/>. The command endpoint softens some of those
    /// refusals to <c>200 { "newEntityId": null }</c> for the contract it predates; nothing here has
    /// to, so a protected-narrator or unknown-target gesture now says why it did nothing instead of
    /// looking like it worked.
    /// </para>
    /// </summary>
    public class CharacterPresenter(
        IProjectReader reader,
        BookMutations mutations,
        CharacterResolver characters,
        IVoiceAudioRemover voiceAudio,
        VoiceOrchestrator voiceOrchestrator,
        Read2Me.Services.Events.EventBroadcaster<Read2Me.Services.Llm.LlmStreamEvent> llmEvents)
    {
        public bool IsLoading { get; private set; }
        public bool IsBusy { get; private set; }
        public string? Error { get; private set; }
        public List<Character> Characters { get; private set; } = [];
        public NarratorIdentity Narrator { get; private set; } = NarratorIdentity.Unlinked;
        public Character? SelectedCharacter { get; private set; }
        public List<CharacterLine> Lines { get; private set; } = [];
        public List<VoiceEntity> Voices { get; private set; } = [];
        public Guid? DefaultVoiceId { get; private set; }
        public List<VoiceRuleRow> VoiceRules { get; private set; } = [];

        internal ProjectFolderId? _folderId;

        /// <summary>
        /// Per-voice cache-buster token. Incremented whenever a voice's audio file is
        /// (over)written so the UI can request a fresh URL. The audio file keeps the same
        /// name across regenerations, so without this the browser and the static-file
        /// middleware's ETag/Last-Modified revalidation serve the stale file until restart.
        /// </summary>
        private readonly Dictionary<Guid, int> _audioTokens = [];

        public int AudioToken(Guid voiceId) => _audioTokens.GetValueOrDefault(voiceId);

        /// <summary>
        /// Public because the voice audio editor writes a voice's WAV without going through this
        /// presenter (Apply overwrites the file in place, under the same name), so it has to bump the
        /// token itself or the characters tab keeps playing the pre-edit audio.
        /// </summary>
        public void BumpAudioToken(Guid voiceId) =>
            _audioTokens[voiceId] = _audioTokens.GetValueOrDefault(voiceId) + 1;

        public event Action? StateChanged;

        public async Task LoadAsync(ProjectFolderId folderId)
        {
            _folderId = folderId;
            IsLoading = true;
            NotifyStateChanged();
            Narrator = await reader.GetNarratorAsync(folderId);
            if (string.IsNullOrEmpty(Narrator.DisplayName))
                Narrator = NarratorIdentity.Unlinked;
            Characters = await reader.GetCharactersWithAliasesAsync(folderId);
            if (SelectedCharacter is not null)
            {
                var reselected = Characters.Find(c => c.Id == SelectedCharacter.Id);
                SelectedCharacter = reselected;
                if (reselected is not null)
                {
                    Lines = await reader.GetCharacterLinesAsync(folderId, reselected.Id);
                    Voices = await reader.GetCharacterVoicesAsync(folderId, reselected.Id);
                    DefaultVoiceId = await reader.GetDefaultVoiceIdAsync(folderId, reselected.Id);
                    VoiceRules = await reader.GetCharacterVoiceRulesAsync(folderId, reselected.Id);
                }
                else
                {
                    Lines = [];
                    Voices = [];
                    DefaultVoiceId = null;
                    VoiceRules = [];
                }
            }
            IsLoading = false;
            NotifyStateChanged();
        }

        public async Task SelectCharacterAsync(Character character)
        {
            if (_folderId is not { } folder) return;
            SelectedCharacter = character;
            Lines = await reader.GetCharacterLinesAsync(folder, character.Id);
            Voices = await reader.GetCharacterVoicesAsync(folder, character.Id);
            DefaultVoiceId = await reader.GetDefaultVoiceIdAsync(folder, character.Id);
            VoiceRules = await reader.GetCharacterVoiceRulesAsync(folder, character.Id);
            NotifyStateChanged();
        }

        // ── Character mutations ────────────────────────────────────────────────

        /// <summary>
        /// Adds a character, or resolves the one who already answers to the name — the same
        /// idempotent-by-name gesture the discovery dialog applies row by row.
        /// </summary>
        public Task AddCharacterAsync(string name) =>
            ExecuteAndReloadAsync(async (folder, ct) =>
                (await characters.ResolveOrCreateWithOutcomeAsync(folder, name, ct)).Outcome);

        public Task AddAliasAsync(Guid characterId, string name) =>
            ExecuteAndReloadAsync(folder => new AddCharacterAliasMutation(folder, characterId, name));

        public Task RemoveAliasAsync(Guid aliasId) =>
            ExecuteAndReloadAsync(folder => new RemoveCharacterAliasMutation(folder, aliasId));

        public Task MergeAsync(Guid survivorId, Guid mergedId, bool addNameAsAlias) =>
            ExecuteAndReloadAsync(folder =>
                new MergeCharactersMutation(folder, survivorId, mergedId, addNameAsAlias));

        public Task DeleteCharacterAsync(Guid characterId) =>
            ExecuteAndReloadAsync(folder => new DeleteCharacterMutation(folder, characterId));

        public Task SetNarratorCharacterAsync(Guid? characterId) =>
            ExecuteAndReloadAsync(folder => new SetNarratorCharacterMutation(folder, characterId));

        public Task RenameCharacterAsync(Guid characterId, string name) =>
            ExecuteAndReloadAsync(folder => new RenameCharacterMutation(folder, characterId, name));

        /// <summary>
        /// Applies accepted rows from the character-discovery review dialog: one resolve-or-create
        /// per included row (idempotent — answers with the existing id on a name/alias match)
        /// followed by one add-alias mutation per alias on that row (deduped by the mutation).
        /// Excluded rows produce nothing.
        /// </summary>
        public async Task ApplyDiscoveredCharactersAsync(
            IReadOnlyList<DiscoveredCharacterRow> rows, CancellationToken ct = default)
        {
            if (_folderId is not { } folder) return;
            IsBusy = true;
            Error = null;
            NotifyStateChanged();
            try
            {
                foreach (var row in rows.Where(r => r.Included))
                    Accepted(await characters.ApplyDiscoveredAsync(folder, row.Name, row.Aliases, ct));
            }
            catch (Exception ex)
            {
                Error = ex.Message;
            }
            IsBusy = false;
            await LoadAsync(folder);
        }

        // ── Voice mutations ─────────────────────────────────────────────────

        public Task AddVoiceAsync(Guid characterId, string name) =>
            ExecuteAndReloadAsync(folder => new CreateVoiceMutation(folder, characterId, name));

        /// <summary>Creates a voice and answers with its new id, reloading as every gesture does.</summary>
        public async Task<Guid?> AddVoiceAndGetIdAsync(Guid characterId, string name, bool isGenerated = false)
        {
            if (_folderId is not { } folder) return null;
            IsBusy = true;
            Error = null;
            NotifyStateChanged();
            Guid? id = null;
            try
            {
                var outcome = await mutations.CommitAsync(
                    new CreateVoiceMutation(folder, characterId, name, isGenerated));
                if (Accepted(outcome) && outcome is BookMutationOutcome.Committed committed)
                    id = committed.Receipt.Effects.CreatedId;
            }
            catch (Exception ex)
            {
                Error = ex.Message;
            }
            IsBusy = false;
            await LoadAsync(folder);
            return id;
        }

        public Task SetVoiceTranscriptDirectAsync(Guid voiceId, string transcript) =>
            ExecuteInPlaceAsync(
                folder => new SetVoiceTranscriptMutation(folder, voiceId, transcript),
                voiceId, v => v.Transcript = transcript);

        public Task SetVoiceSourceAsync(Guid voiceId, bool isGenerated) =>
            // Through the audio remover, not straight to BookMutations: a Voice that has become
            // designed stops naming its recording, and the file goes after that commits (ADR 0007).
            ExecuteAndReloadAsync((folder, ct) =>
                voiceAudio.SetVoiceSourceAsync(folder, voiceId, isGenerated, ct));

        public Task SetVoiceDesignPromptDirectAsync(Guid voiceId, string prompt) =>
            ExecuteAndReloadAsync(folder => new SetVoiceDesignPromptMutation(folder, voiceId, prompt));

        public Task SetVoiceSettingsOverrideAsync(Guid voiceId, string? json) =>
            ExecuteInPlaceAsync(
                folder => new SetVoiceDesignSettingsOverrideMutation(folder, voiceId, json),
                voiceId, v => v.VoiceDesignSettingsOverrideJson = json);

        public Task SetVoiceTtsSettingsOverrideAsync(Guid voiceId, string? json) =>
            ExecuteInPlaceAsync(
                folder => new SetVoiceTtsSettingsOverrideMutation(folder, voiceId, json),
                voiceId, v => v.TtsSettingsOverrideJson = json);

        public Task SetVoiceDefaultAsync(Guid voiceId) =>
            ExecuteAndReloadAsync(folder => new SetVoiceDefaultMutation(folder, voiceId));

        public Task UpdateVoiceAsync(Guid voiceId, string name, string? description) =>
            ExecuteAndReloadAsync(folder => new UpdateVoiceMutation(folder, voiceId, name, description));

        public Task DeleteVoiceAsync(Guid voiceId) =>
            // Same ordering as a source flip: the Book stops naming the recording first, the file
            // goes afterwards.
            ExecuteAndReloadAsync((folder, ct) => voiceAudio.DeleteVoiceAsync(folder, voiceId, ct));

        // ── Voice Rule mutations ───────────────────────────────────────────────

        public Task CreateVoiceRuleAsync(
            Guid characterId, Guid voiceId,
            VoiceAnchorLevel? fromLevel, Guid? fromNodeId,
            VoiceAnchorLevel? toLevel, Guid? toNodeId) =>
            ExecuteAndReloadAsync(folder => new CreateVoiceRuleMutation(
                folder, characterId, voiceId, fromLevel, fromNodeId, toLevel, toNodeId));

        public Task DeleteVoiceRuleAsync(Guid ruleId) =>
            ExecuteAndReloadAsync(folder => new DeleteVoiceRuleMutation(folder, ruleId));

        public Task MoveVoiceRuleUpAsync(Guid ruleId) =>
            ExecuteAndReloadAsync(folder => new MoveVoiceRuleMutation(folder, ruleId, RuleMoveDirection.Up));

        public Task MoveVoiceRuleDownAsync(Guid ruleId) =>
            ExecuteAndReloadAsync(folder => new MoveVoiceRuleMutation(folder, ruleId, RuleMoveDirection.Down));

        // ── Voice orchestration (AI + file I/O + DB) ─────────────────────────

        public async Task UploadVoiceAudioAsync(
            Guid characterId, Guid voiceId, string voiceName,
            System.IO.Stream audioStream, string extension,
            CancellationToken ct = default)
        {
            if (_folderId is not { } folder) return;

            var character = Characters.Find(c => c.Id == characterId);
            IsBusy = true;
            Error = null;
            NotifyStateChanged();
            try
            {
                var req = new Read2Me.Core.Audio.AudioStoreRequest
                {
                    FolderId = folder,
                    CharacterId = characterId,
                    CharacterName = character?.Name ?? string.Empty,
                    CharacterAliases = character?.Aliases is { } aliases
                        ? aliases.Select(a => a.Name).ToList()
                        : [],
                    VoiceId = voiceId,
                    VoiceName = voiceName,
                    Source = audioStream,
                    Extension = extension,
                };
                // One call: the recording is stored and the Book mutation that names it is committed
                // together, so a refused write leaves neither a file nobody names nor a name with no
                // file (ADR 0007).
                await voiceOrchestrator.RecordUploadedAudioAsync(req, ct);
                BumpAudioToken(voiceId);
            }
            catch (Exception ex)
            {
                Error = ex.Message;
            }
            IsBusy = false;
            await LoadAsync(folder);
        }

        public Task ReplaceVoiceAudioAsync(
            Guid characterId, Guid voiceId, string voiceName,
            System.IO.Stream audioStream, string extension,
            CancellationToken ct = default) =>
            UploadVoiceAudioAsync(characterId, voiceId, voiceName, audioStream, extension, ct);

        public System.IO.Stream? OpenVoiceAudioStream(VoiceEntity voice)
        {
            if (_folderId is not { } folder) return null;
            return voiceOrchestrator.OpenAudioStream(folder, voice.AudioFileName);
        }

        public async Task TranscribeVoiceAsync(
            Guid voiceId, System.IO.Stream audioStream, string fileName,
            CancellationToken ct = default)
        {
            if (_folderId is not { } folder) return;

            IsBusy = true;
            Error = null;
            NotifyStateChanged();
            try
            {
                var transcript = await voiceOrchestrator.TranscribeAsync(folder, voiceId, audioStream, fileName, ct);
                if (Accepted(await mutations.CommitAsync(
                        new SetVoiceTranscriptMutation(folder, voiceId, transcript), ct)))
                    UpdateVoiceInPlace(voiceId, v => v.Transcript = transcript);
            }
            catch (Exception ex)
            {
                Error = ex.Message;
            }
            IsBusy = false;
            NotifyStateChanged();
        }

        public static int ReadyVoiceCount(Character character) =>
            character.Voices?.Count(v => !string.IsNullOrEmpty(v.AudioFileName)) ?? 0;

        /// <summary>
        /// Applies a mutation to the in-memory <see cref="VoiceEntity"/> with the
        /// given id (in <see cref="Voices"/>, the selected character's voice list, and
        /// every character's voice list) so a single-field change doesn't require
        /// reloading every character, line, and voice from the database — which would
        /// replace all objects and reset transient UI state (expanded panels, drafts).
        /// </summary>
        private void UpdateVoiceInPlace(Guid voiceId, Action<VoiceEntity> mutate)
        {
            var voice = Voices.Find(v => v.Id == voiceId);
            if (voice is not null) mutate(voice);

            var charVoice = SelectedCharacter?.Voices?.FirstOrDefault(v => v.Id == voiceId);
            if (charVoice is not null && !ReferenceEquals(charVoice, voice)) mutate(charVoice);

            foreach (var c in Characters)
            {
                if (ReferenceEquals(c, SelectedCharacter)) continue;
                var cv = c.Voices?.FirstOrDefault(v => v.Id == voiceId);
                if (cv is not null) mutate(cv);
            }
        }

        /// <summary>
        /// Builds the rendered LLM prompt for voice design without calling the LLM.
        /// Returns null if no folder is active.
        /// </summary>
        public async Task<string?> BuildDesignPromptAsync(Guid characterId)
        {
            if (_folderId is not { } folder) return null;
            var character = Characters.Find(c => c.Id == characterId);
            var project = await reader.GetProjectAsync(folder);
            return await voiceOrchestrator.BuildRenderedPromptAsync(
                project?.BookTitle ?? string.Empty,
                project?.Author ?? string.Empty,
                character?.Name ?? string.Empty);
        }

        /// <summary>
        /// Sends a pre-built (possibly user-edited) prompt to the LLM for voice design.
        /// Does not persist anything — caller decides what to do with the result.
        /// </summary>
        public async Task<string?> GenerateDesignPromptWithTextAsync(
            string renderedPrompt,
            CancellationToken ct = default)
        {
            // A single voice design is a Throughput Run of one. The batch path never comes
            // through here — it brackets its whole sweep as one run in VoiceBatchRunner.
            llmEvents.Publish(new Read2Me.Services.Llm.RunStarted());
            try
            {
                return await voiceOrchestrator.GenerateWithPromptAsync(renderedPrompt, ct);
            }
            catch (Exception ex)
            {
                Error = ex.Message;
                NotifyStateChanged();
                return null;
            }
            finally
            {
                llmEvents.Publish(new Read2Me.Services.Llm.RunEnded());
            }
        }

        public async Task GenerateVoiceAudioAsync(
            Guid characterId, Guid voiceId, string voiceName, string designPrompt,
            CancellationToken ct = default)
        {
            if (_folderId is not { } folder) return;

            var character = Characters.Find(c => c.Id == characterId);

            IsBusy = true;
            Error = null;
            NotifyStateChanged();
            try
            {
                var request = new Read2Me.Services.Audio.VoiceDesign.VoiceGenerationRequest
                {
                    FolderId = folder,
                    CharacterId = characterId,
                    CharacterName = character?.Name ?? string.Empty,
                    CharacterAliases = character?.Aliases?.Select(a => a.Name).ToList() ?? [],
                    VoiceId = voiceId,
                    VoiceName = voiceName,
                    DesignPrompt = designPrompt,
                    SettingsOverrideJson = Voices.Find(v => v.Id == voiceId)?.VoiceDesignSettingsOverrideJson
                };

                var result = await voiceOrchestrator.GenerateVoiceAudioAsync(request, ct);

                if (result.IsSuccess)
                {
                    UpdateVoiceInPlace(voiceId, v =>
                    {
                        v.AudioFileName = result.AudioFileName;
                        v.Transcript = result.Transcript;
                        v.DesignPrompt = designPrompt;
                    });
                    BumpAudioToken(voiceId);
                }
                else
                {
                    Error = result.ErrorMessage;
                }
            }
            catch (Exception ex)
            {
                Error = ex.Message;
            }
            IsBusy = false;
            NotifyStateChanged();
        }

        public void ApplyVoiceUpdate(Guid voiceId, string? designPrompt, string? audioFileName, string? transcript)
        {
            UpdateVoiceInPlace(voiceId, v =>
            {
                if (designPrompt is not null) v.DesignPrompt = designPrompt;
                if (audioFileName is not null) { v.AudioFileName = audioFileName; BumpAudioToken(voiceId); }
                if (transcript is not null) v.Transcript = transcript;
            });
            NotifyStateChanged();
        }

        // ── Context helper ────────────────────────────────────────────────────

        public async Task<ParagraphContext?> GetLineContextAsync(CharacterLine line, int before, int after)
        {
            if (_folderId is not { } folder) return null;
            return await reader.GetParagraphContextAsync(folder, line.ChapterId, line.ParagraphId, before, after);
        }

        // ── Internal ──────────────────────────────────────────────────────────

        private Task ExecuteAndReloadAsync(Func<ProjectFolderId, BookMutation> mutation) =>
            ExecuteAndReloadAsync((folder, ct) => mutations.CommitAsync(mutation(folder), ct));

        /// <summary>
        /// Commits one Book mutation and rereads the page from the persisted Book afterwards, which
        /// is where every gesture on this tab ends: the reads it renders are not on a receipt.
        /// </summary>
        private async Task ExecuteAndReloadAsync(
            Func<ProjectFolderId, CancellationToken, Task<BookMutationOutcome>> commit)
        {
            if (_folderId is not { } folder) return;
            IsBusy = true;
            Error = null;
            NotifyStateChanged();
            try
            {
                Accepted(await commit(folder, CancellationToken.None));
            }
            catch (Exception ex)
            {
                Error = ex.Message;
            }
            IsBusy = false;
            await LoadAsync(folder);
        }

        /// <summary>
        /// The one-field gestures that patch the loaded <see cref="VoiceEntity"/> instead of
        /// reloading, because a full reread would replace every object the open panels and unsent
        /// drafts are bound to. The patch is applied only if the write took.
        /// </summary>
        private async Task ExecuteInPlaceAsync(
            Func<ProjectFolderId, BookMutation> mutation, Guid voiceId, Action<VoiceEntity> patch)
        {
            if (_folderId is not { } folder) return;
            IsBusy = true;
            Error = null;
            NotifyStateChanged();
            try
            {
                if (Accepted(await mutations.CommitAsync(mutation(folder))))
                    UpdateVoiceInPlace(voiceId, patch);
            }
            catch (Exception ex)
            {
                Error = ex.Message;
            }
            IsBusy = false;
            NotifyStateChanged();
        }

        /// <summary>
        /// Turns a refusal into the page's <see cref="Error"/>. Answers whether the gesture is worth
        /// carrying on from — a mutation that changed nothing legally did, a refused one did not.
        /// </summary>
        private bool Accepted(BookMutationOutcome outcome)
        {
            if (outcome is not BookMutationOutcome.Rejected rejected) return true;
            Error = rejected.Message;
            return false;
        }

        private void NotifyStateChanged() => StateChanged?.Invoke();
    }
}
