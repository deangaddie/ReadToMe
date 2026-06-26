using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Read2Me.App.Services;
using Read2Me.Core.Models;
using Read2Me.Data.Enums;
using Read2Me.Services;
using Read2Me.Services.Audio.VoiceDesign;

namespace Read2Me.App.Characters;

public sealed class CharacterBatchService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<CharacterBatchService> _logger;

    public event Action<VoiceBatchEvent>? BatchEvent;

    private readonly object _lock = new();
    private CancellationTokenSource? _cts;

    public bool IsRunning { get; private set; }
    public int Processed { get; private set; }
    public int Total { get; private set; }
    public int Failed { get; private set; }
    public string? CurrentVoiceName { get; private set; }
    public string? CurrentOperation { get; private set; }
    public string? LastError { get; private set; }

    public CharacterBatchService(
        IServiceScopeFactory scopeFactory,
        ILogger<CharacterBatchService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public bool StartGeneratePrompts(ProjectFolderId folder)
    {
        lock (_lock)
        {
            if (IsRunning) return false;

            IsRunning = true;
            Processed = 0;
            Total = 0;
            Failed = 0;
            CurrentVoiceName = null;
            CurrentOperation = "Generating prompts";
            LastError = null;

            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            Task.Run(() => RunGeneratePromptsAsync(folder, ct));
        }
        return true;
    }

    public bool StartGenerateAudio(ProjectFolderId folder)
    {
        lock (_lock)
        {
            if (IsRunning) return false;

            IsRunning = true;
            Processed = 0;
            Total = 0;
            Failed = 0;
            CurrentVoiceName = null;
            CurrentOperation = "Generating audio";
            LastError = null;

            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            Task.Run(() => RunGenerateAudioAsync(folder, ct));
        }
        return true;
    }

    public void Cancel()
    {
        lock (_lock)
            _cts?.Cancel();
    }

    private async Task RunGeneratePromptsAsync(ProjectFolderId folder, CancellationToken ct)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var reader = scope.ServiceProvider.GetRequiredService<IProjectReader>();
            var commandHandler = scope.ServiceProvider.GetRequiredService<IBookCommandHandler>();
            var orchestrator = scope.ServiceProvider.GetRequiredService<VoiceOrchestrator>();

            ct.ThrowIfCancellationRequested();

            var project = await reader.GetProjectAsync(folder);
            var bookTitle = project?.BookTitle ?? folder.Value;
            var author = project?.Author ?? string.Empty;

            var characters = await reader.GetCharactersWithAliasesAsync(folder);

            BatchEvent?.Invoke(new BatchStarted("Generating prompts", characters.Count));

            // Phase 1: ensure every character has at least one voice
            foreach (var character in characters)
            {
                ct.ThrowIfCancellationRequested();

                var voices = await reader.GetCharacterVoicesAsync(folder, character.Id);
                if (voices.Count == 0)
                {
                    await commandHandler.ExecuteAsync(
                        new CreateVoiceCommand(folder, character.Id, "Default", IsGenerated: true), ct);
                }
            }

            // Phase 2: generate prompts for all blank Prompt-type voices
            // Re-read voices so newly created Defaults are visible
            var voicesToPrompt = new List<(Guid CharacterId, Guid VoiceId, string CharacterName)>();
            foreach (var character in characters)
            {
                ct.ThrowIfCancellationRequested();

                var voices = await reader.GetCharacterVoicesAsync(folder, character.Id);
                foreach (var voice in voices)
                {
                    if (voice.Source == VoiceSource.Generated && string.IsNullOrWhiteSpace(voice.DesignPrompt))
                        voicesToPrompt.Add((character.Id, voice.Id, character.Name));
                }
            }

            lock (_lock) { Total = voicesToPrompt.Count; }

            foreach (var (characterId, voiceId, characterName) in voicesToPrompt)
            {
                ct.ThrowIfCancellationRequested();

                lock (_lock) { CurrentVoiceName = characterName; }
                BatchEvent?.Invoke(new BatchProgress(Processed, Total, Failed, characterName));

                try
                {
                    var renderedPrompt = await orchestrator.BuildRenderedPromptAsync(bookTitle, author, characterName);
                    var designPrompt = await orchestrator.GenerateWithPromptAsync(renderedPrompt, ct);

                    await commandHandler.ExecuteAsync(
                        new SetVoiceDesignPromptCommand(folder, voiceId, designPrompt), ct);

                    lock (_lock) { Processed++; }
                    BatchEvent?.Invoke(new VoiceUpdated(characterId, voiceId, designPrompt, null, null));
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to generate prompt for voice {VoiceId} of character {CharacterName}",
                        voiceId, characterName);
                    lock (_lock) { Failed++; Processed++; }
                }
            }

            lock (_lock)
            {
                IsRunning = false;
                CurrentVoiceName = null;
                CurrentOperation = null;
            }
            BatchEvent?.Invoke(new BatchCompleted(Processed - Failed, Failed));
        }
        catch (OperationCanceledException)
        {
            lock (_lock)
            {
                IsRunning = false;
                CurrentVoiceName = null;
                CurrentOperation = null;
            }
            BatchEvent?.Invoke(new BatchCancelled());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CharacterBatchService prompt sweep failed for {Folder}", folder.Value);
            lock (_lock)
            {
                IsRunning = false;
                CurrentVoiceName = null;
                CurrentOperation = null;
                LastError = ex.Message;
            }
            BatchEvent?.Invoke(new BatchCompleted(Processed - Failed, Failed));
        }
    }

    private async Task RunGenerateAudioAsync(ProjectFolderId folder, CancellationToken ct)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var reader = scope.ServiceProvider.GetRequiredService<IProjectReader>();
            var commandHandler = scope.ServiceProvider.GetRequiredService<IBookCommandHandler>();
            var orchestrator = scope.ServiceProvider.GetRequiredService<VoiceOrchestrator>();

            ct.ThrowIfCancellationRequested();

            var characters = await reader.GetCharactersWithAliasesAsync(folder);

            var voicesToGenerate = new List<(Guid CharacterId, string CharacterName, IReadOnlyList<string> Aliases, Guid VoiceId, string VoiceName, string DesignPrompt, string? SettingsOverrideJson)>();
            foreach (var character in characters)
            {
                ct.ThrowIfCancellationRequested();

                var voices = await reader.GetCharacterVoicesAsync(folder, character.Id);
                foreach (var voice in voices)
                {
                    if (voice.Source == VoiceSource.Generated
                        && !string.IsNullOrWhiteSpace(voice.DesignPrompt)
                        && string.IsNullOrWhiteSpace(voice.AudioFileName))
                    {
                        voicesToGenerate.Add((
                            character.Id,
                            character.Name,
                            character.Aliases?.Select(a => a.Name).ToList() ?? [],
                            voice.Id,
                            voice.Name,
                            voice.DesignPrompt!,
                            voice.VoiceDesignSettingsOverrideJson));
                    }
                }
            }

            lock (_lock) { Total = voicesToGenerate.Count; }
            BatchEvent?.Invoke(new BatchStarted("Generating audio", voicesToGenerate.Count));

            foreach (var (characterId, characterName, aliases, voiceId, voiceName, designPrompt, settingsOverrideJson) in voicesToGenerate)
            {
                ct.ThrowIfCancellationRequested();

                lock (_lock) { CurrentVoiceName = characterName; }
                BatchEvent?.Invoke(new BatchProgress(Processed, Total, Failed, characterName));

                try
                {
                    var request = new VoiceGenerationRequest
                    {
                        FolderId = folder,
                        CharacterId = characterId,
                        CharacterName = characterName,
                        CharacterAliases = aliases,
                        VoiceId = voiceId,
                        VoiceName = voiceName,
                        DesignPrompt = designPrompt,
                        SettingsOverrideJson = settingsOverrideJson,
                    };

                    var result = await orchestrator.GenerateVoiceAudioAsync(request, ct);

                    if (result.IsSuccess)
                    {
                        lock (_lock) { Processed++; }
                        BatchEvent?.Invoke(new VoiceUpdated(characterId, voiceId, null, result.AudioFileName, result.Transcript));
                    }
                    else
                    {
                        _logger.LogWarning("Failed to generate audio for voice {VoiceId} of character {CharacterName}: {Error}",
                            voiceId, characterName, result.ErrorMessage);
                        lock (_lock) { Failed++; Processed++; }
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to generate audio for voice {VoiceId} of character {CharacterName}",
                        voiceId, characterName);
                    lock (_lock) { Failed++; Processed++; }
                }
            }

            lock (_lock)
            {
                IsRunning = false;
                CurrentVoiceName = null;
                CurrentOperation = null;
            }
            BatchEvent?.Invoke(new BatchCompleted(Processed - Failed, Failed));
        }
        catch (OperationCanceledException)
        {
            lock (_lock)
            {
                IsRunning = false;
                CurrentVoiceName = null;
                CurrentOperation = null;
            }
            BatchEvent?.Invoke(new BatchCancelled());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CharacterBatchService audio sweep failed for {Folder}", folder.Value);
            lock (_lock)
            {
                IsRunning = false;
                CurrentVoiceName = null;
                CurrentOperation = null;
                LastError = ex.Message;
            }
            BatchEvent?.Invoke(new BatchCompleted(Processed - Failed, Failed));
        }
    }
}
