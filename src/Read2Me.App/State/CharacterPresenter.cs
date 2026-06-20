using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Read2Me.Core.Audio;
using Read2Me.Core.Models;
using Read2Me.Data.Entities;
using Read2Me.Services;
using Read2Me.Services.Audio.Transcription;
using Read2Me.Services.Audio.VoiceDesign;
using Read2Me.Services.Llm;
using VoiceEntity = Read2Me.Data.Entities.Voice;

namespace Read2Me.App.State
{
    public class CharacterPresenter(
        IProjectReader reader,
        IBookCommandHandler commandHandler,
        IAudioPipeline audioPipeline,
        ITranscriptionClientResolver transcriptionResolver,
        VoiceAudioGenerator voiceAudioGenerator,
        TranscriptionSettingsService transcriptionSettings,
        Read2Me.Services.Voice.VoiceDesignPromptService voiceDesignPromptService,
        Read2Me.Core.IO.IFileSystem fileSystem)
    {
        public bool IsLoading { get; private set; }
        public bool IsBusy { get; private set; }
        public string? Error { get; private set; }
        public List<Character> Characters { get; private set; } = [];
        public Character? SelectedCharacter { get; private set; }
        public List<CharacterLine> Lines { get; private set; } = [];
        public List<VoiceEntity> Voices { get; private set; } = [];

        private ProjectFolderId? _folderId;

        /// <summary>
        /// Per-voice cache-buster token. Incremented whenever a voice's audio file is
        /// (over)written so the UI can request a fresh URL. The audio file keeps the same
        /// name across regenerations, so without this the browser and the static-file
        /// middleware's ETag/Last-Modified revalidation serve the stale file until restart.
        /// </summary>
        private readonly Dictionary<Guid, int> _audioTokens = [];

        public int AudioToken(Guid voiceId) => _audioTokens.GetValueOrDefault(voiceId);

        private void BumpAudioToken(Guid voiceId) =>
            _audioTokens[voiceId] = _audioTokens.GetValueOrDefault(voiceId) + 1;

        public event Action? StateChanged;

        public async Task LoadAsync(ProjectFolderId folderId)
        {
            _folderId = folderId;
            IsLoading = true;
            NotifyStateChanged();
            Characters = await reader.GetCharactersWithAliasesAsync(folderId);
            if (SelectedCharacter is not null)
            {
                var reselected = Characters.Find(c => c.Id == SelectedCharacter.Id);
                SelectedCharacter = reselected;
                if (reselected is not null)
                {
                    Lines = await reader.GetCharacterLinesAsync(folderId, reselected.Id);
                    Voices = await reader.GetCharacterVoicesAsync(folderId, reselected.Id);
                }
                else
                {
                    Lines = [];
                    Voices = [];
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
            NotifyStateChanged();
        }

        // ── Character commands ────────────────────────────────────────────────

        public Task AddCharacterAsync(string name) =>
            ExecuteAndReloadAsync(new CreateCharacterCommand(_folderId!.Value, name));

        public Task AddAliasAsync(Guid characterId, string name) =>
            ExecuteAndReloadAsync(new AddCharacterAliasCommand(_folderId!.Value, characterId, name));

        public Task RemoveAliasAsync(Guid aliasId) =>
            ExecuteAndReloadAsync(new RemoveCharacterAliasCommand(_folderId!.Value, aliasId));

        public Task MergeAsync(Guid survivorId, Guid mergedId, bool addNameAsAlias) =>
            ExecuteAndReloadAsync(new MergeCharactersCommand(_folderId!.Value, survivorId, mergedId, addNameAsAlias));

        public Task DeleteCharacterAsync(Guid characterId) =>
            ExecuteAndReloadAsync(new DeleteCharacterCommand(_folderId!.Value, characterId));

        // ── Voice DB commands ─────────────────────────────────────────────────

        public Task AddVoiceAsync(Guid characterId, string name) =>
            ExecuteAndReloadAsync(new CreateVoiceCommand(_folderId!.Value, characterId, name));

        /// <summary>Creates a voice and returns its new ID without triggering a full reload.</summary>
        public async Task<Guid?> AddVoiceAndGetIdAsync(Guid characterId, string name, bool isGenerated = false)
        {
            if (_folderId is not { } folder) return null;
            IsBusy = true;
            Error = null;
            NotifyStateChanged();
            Guid? id = null;
            try
            {
                id = await commandHandler.ExecuteAsync(new CreateVoiceCommand(folder, characterId, name, isGenerated));
            }
            catch (Exception ex)
            {
                Error = ex.Message;
            }
            IsBusy = false;
            await LoadAsync(folder);
            return id;
        }

        public async Task SetVoiceTranscriptDirectAsync(Guid voiceId, string transcript)
        {
            if (_folderId is not { } folder) return;
            IsBusy = true;
            Error = null;
            NotifyStateChanged();
            try
            {
                await commandHandler.ExecuteAsync(new SetVoiceTranscriptCommand(folder, voiceId, transcript));
                UpdateVoiceInPlace(voiceId, v => v.Transcript = transcript);
            }
            catch (Exception ex)
            {
                Error = ex.Message;
            }
            IsBusy = false;
            NotifyStateChanged();
        }

        public Task SetVoiceSourceAsync(Guid voiceId, bool isGenerated) =>
            ExecuteAndReloadAsync(new SetVoiceSourceCommand(_folderId!.Value, voiceId, isGenerated));

        public Task SetVoiceDesignPromptDirectAsync(Guid voiceId, string prompt) =>
            ExecuteAndReloadAsync(new SetVoiceDesignPromptCommand(_folderId!.Value, voiceId, prompt));

        public async Task SetVoiceSettingsOverrideAsync(Guid voiceId, string? json)
        {
            if (_folderId is not { } folder) return;
            IsBusy = true; Error = null; NotifyStateChanged();
            try
            {
                await commandHandler.ExecuteAsync(new SetVoiceSettingsOverrideCommand(folder, voiceId, json));
                UpdateVoiceInPlace(voiceId, v => v.SettingsOverrideJson = json);
            }
            catch (Exception ex) { Error = ex.Message; }
            IsBusy = false; NotifyStateChanged();
        }

        public Task SetVoiceDefaultAsync(Guid voiceId) =>
            ExecuteAndReloadAsync(new SetVoiceDefaultCommand(_folderId!.Value, voiceId));

        public Task UpdateVoiceAsync(Guid voiceId, string name, string? description) =>
            ExecuteAndReloadAsync(new UpdateVoiceCommand(_folderId!.Value, voiceId, name, description));

        public Task DeleteVoiceAsync(Guid voiceId) =>
            ExecuteAndReloadAsync(new DeleteVoiceCommand(_folderId!.Value, voiceId));

        // ── Voice orchestration (AI + file I/O + DB) ─────────────────────────

        public async Task UploadVoiceAudioAsync(
            Guid characterId, Guid voiceId, string voiceName,
            Stream audioStream, string extension,
            CancellationToken ct = default)
        {
            if (_folderId is not { } folder) return;

            var character = Characters.Find(c => c.Id == characterId);
            IsBusy = true;
            Error = null;
            NotifyStateChanged();
            try
            {
                var req = new AudioStoreRequest
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
                var fileName = await audioPipeline.StoreAsync(req, ct);
                await commandHandler.ExecuteAsync(new SetVoiceAudioCommand(folder, voiceId, fileName), ct);
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
            Stream audioStream, string extension,
            CancellationToken ct = default) =>
            UploadVoiceAudioAsync(characterId, voiceId, voiceName, audioStream, extension, ct);

        public Stream? OpenVoiceAudioStream(VoiceEntity voice)
        {
            if (_folderId is not { } folder || voice.AudioFileName == null) return null;
            var projectFolder = fileSystem.GetProjectFolderPath(folder.Value);
            var path = System.IO.Path.Combine(projectFolder, voice.AudioFileName.Replace('/', System.IO.Path.DirectorySeparatorChar));
            if (!fileSystem.FileExists(path)) return null;
            return File.OpenRead(path);
        }

        public async Task TranscribeVoiceAsync(
            Guid voiceId, Stream audioStream, string fileName,
            CancellationToken ct = default)
        {
            if (_folderId is not { } folder) return;

            var config = await transcriptionSettings.GetActiveConfigAsync();
            if (config == null)
            {
                Error = "No active transcription server configured.";
                NotifyStateChanged();
                return;
            }

            IsBusy = true;
            Error = null;
            NotifyStateChanged();
            try
            {
                var transcriptionClient = transcriptionResolver.Resolve(config.Type);
                var transcript = await transcriptionClient.TranscribeAsync(config, audioStream, fileName, ct);
                await commandHandler.ExecuteAsync(new SetVoiceTranscriptCommand(folder, voiceId, transcript), ct);
                UpdateVoiceInPlace(voiceId, v => v.Transcript = transcript);
            }
            catch (Exception ex)
            {
                Error = ex.Message;
            }
            IsBusy = false;
            NotifyStateChanged();
        }

        /// <summary>
        /// Applies a mutation to the in-memory <see cref="VoiceEntity"/> with the
        /// given id (in both <see cref="Voices"/> and the selected character's voice
        /// list) so a single-field change doesn't require reloading every character,
        /// line, and voice from the database — which would replace all objects and
        /// reset transient UI state (expanded panels, drafts).
        /// </summary>
        private void UpdateVoiceInPlace(Guid voiceId, Action<VoiceEntity> mutate)
        {
            var voice = Voices.Find(v => v.Id == voiceId);
            if (voice is not null) mutate(voice);

            var charVoice = SelectedCharacter?.Voices?.FirstOrDefault(v => v.Id == voiceId);
            if (charVoice is not null && !ReferenceEquals(charVoice, voice)) mutate(charVoice);
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
            return await voiceDesignPromptService.BuildRenderedPromptAsync(
                project?.BookTitle ?? string.Empty,
                project?.Author ?? string.Empty,
                character?.Name ?? string.Empty);
        }

        /// <summary>
        /// Generates a voice-design prompt from the LLM for the given character and returns it.
        /// Does not persist anything — caller decides what to do with the result.
        /// </summary>
        public async Task<string?> GenerateDesignPromptTextAsync(
            Guid characterId,
            CancellationToken ct = default)
        {
            if (_folderId is not { } folder) return null;
            var character = Characters.Find(c => c.Id == characterId);
            var project = await reader.GetProjectAsync(folder);
            var result = await voiceDesignPromptService.GenerateAsync(
                project?.BookTitle ?? string.Empty,
                project?.Author ?? string.Empty,
                character?.Name ?? string.Empty,
                ct);
            return result.Status == Services.Voice.VoiceDesignPromptService.GenerateStatus.Success
                ? result.Prompt
                : null;
        }

        /// <summary>
        /// Sends a pre-built (possibly user-edited) prompt to the LLM for voice design.
        /// Does not persist anything — caller decides what to do with the result.
        /// </summary>
        public async Task<string?> GenerateDesignPromptWithTextAsync(
            string renderedPrompt,
            CancellationToken ct = default)
        {
            var result = await voiceDesignPromptService.GenerateWithPromptAsync(renderedPrompt, ct);
            return result.Status == Services.Voice.VoiceDesignPromptService.GenerateStatus.Success
                ? result.Prompt
                : null;
        }

        public async Task GenerateDesignPromptAsync(
            Guid characterId, Guid voiceId,
            CancellationToken ct = default)
        {
            if (_folderId is not { } folder) return;

            var character = Characters.Find(c => c.Id == characterId);
            var project = await reader.GetProjectAsync(folder);

            IsBusy = true;
            Error = null;
            NotifyStateChanged();
            try
            {
                var result = await voiceDesignPromptService.GenerateAsync(
                    project?.BookTitle ?? string.Empty,
                    project?.Author ?? string.Empty,
                    character?.Name ?? string.Empty,
                    ct);

                if (result.Status == Services.Voice.VoiceDesignPromptService.GenerateStatus.Success && result.Prompt is not null)
                    await commandHandler.ExecuteAsync(new SetVoiceDesignPromptCommand(folder, voiceId, result.Prompt), ct);
                else
                    Error = result.FailureReason ?? "Failed to generate voice prompt.";
            }
            catch (Exception ex)
            {
                Error = ex.Message;
            }
            IsBusy = false;
            await LoadAsync(folder);
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
                var request = new VoiceGenerationRequest
                {
                    FolderId = folder,
                    CharacterId = characterId,
                    CharacterName = character?.Name ?? string.Empty,
                    CharacterAliases = character?.Aliases?.Select(a => a.Name).ToList() ?? [],
                    VoiceId = voiceId,
                    VoiceName = voiceName,
                    DesignPrompt = designPrompt,
                    SettingsOverrideJson = Voices.Find(v => v.Id == voiceId)?.SettingsOverrideJson
                };

                var result = await voiceAudioGenerator.GenerateAsync(request, ct);

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

        // ── Context helper ────────────────────────────────────────────────────

        public async Task<ParagraphContext?> GetLineContextAsync(CharacterLine line, int before, int after)
        {
            if (_folderId is not { } folder) return null;
            return await reader.GetParagraphContextAsync(folder, line.ChapterId, line.ParagraphId, before, after);
        }

        // ── Internal ──────────────────────────────────────────────────────────

        private async Task ExecuteAndReloadAsync(BookCommand command)
        {
            if (_folderId is not { } folder) return;
            IsBusy = true;
            Error = null;
            NotifyStateChanged();
            try
            {
                await commandHandler.ExecuteAsync(command);
            }
            catch (Exception ex)
            {
                Error = ex.Message;
            }
            IsBusy = false;
            await LoadAsync(folder);
        }

        private void NotifyStateChanged() => StateChanged?.Invoke();
    }
}
