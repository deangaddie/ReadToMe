using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Read2Me.Core.Models;
using Read2Me.Services;

namespace Read2Me.App.Characters;

public sealed class GeneratePromptsPhase : ISweepPhase<PromptWorkItem>
{
    private readonly bool _regenerateAll;
    private ProjectFolderId _folder;
    private string _bookTitle = string.Empty;
    private string _author = string.Empty;

    /// <param name="regenerateAll">
    /// False: plan voices only for characters that have none yet.
    /// True: clear every character's existing voices and re-plan for all.
    /// </param>
    public GeneratePromptsPhase(bool regenerateAll = false) => _regenerateAll = regenerateAll;

    public const string OperationName = "Generating prompts";

    public string Operation => OperationName;

    public bool DrivesLlm => true;

    public string DisplayName(PromptWorkItem item) => item.CharacterName;

    public async Task<IReadOnlyList<PromptWorkItem>> PlanAsync(
        PhaseDeps deps, ProjectFolderId folder, CancellationToken ct)
    {
        _folder = folder;
        var project = await deps.Reader.GetProjectAsync(folder);
        _bookTitle = project?.BookTitle ?? folder.Value;
        _author = project?.Author ?? string.Empty;

        // One work item per character that has no voices yet: the LLM plans the
        // full set of voices for the character in a single call.
        var characters = await deps.Reader.GetCharactersWithAliasesAsync(folder);
        var narrator = await deps.Reader.GetNarratorAsync(folder, ct);
        var workList = new List<PromptWorkItem>();
        foreach (var character in characters)
        {
            ct.ThrowIfCancellationRequested();
            var alsoNarrates = narrator.IsLinked && character.Id == narrator.CharacterId;
            if (_regenerateAll)
            {
                workList.Add(new PromptWorkItem(
                    character.Id, character.Name, character.IsNarrator, alsoNarrates));
                continue;
            }
            var voices = await deps.Reader.GetCharacterVoicesAsync(folder, character.Id);
            if (voices.Count == 0)
                workList.Add(new PromptWorkItem(
                    character.Id, character.Name, character.IsNarrator, alsoNarrates));
        }
        return workList;
    }

    public async Task<PhaseStepOutcome> RunStepAsync(
        PromptWorkItem item, PhaseDeps deps, CancellationToken ct)
    {
        // Regenerate-all: drop the character's existing voices before re-planning.
        if (_regenerateAll)
        {
            var existing = await deps.Reader.GetCharacterVoicesAsync(_folder, item.CharacterId);
            foreach (var voice in existing)
            {
                ct.ThrowIfCancellationRequested();
                await deps.CommandHandler.ExecuteAsync(new DeleteVoiceCommand(_folder, voice.Id), ct);
            }
        }

        var plan = await deps.Orchestrator.GenerateVoicePlanAsync(
            _bookTitle, _author, item.CharacterName, item.IsNarrator, item.AlsoNarrates, ct);

        foreach (var voice in plan)
        {
            ct.ThrowIfCancellationRequested();

            var voiceId = await deps.CommandHandler.ExecuteAsync(
                new CreateVoiceCommand(_folder, item.CharacterId, voice.Name, IsGenerated: true), ct);
            if (voiceId is not { } id)
                return new PhaseStepOutcome(Ok: false, Update: null,
                    FailReason: $"Character {item.CharacterName} no longer exists");

            await deps.CommandHandler.ExecuteAsync(
                new UpdateVoiceCommand(_folder, id, voice.Name, voice.Description), ct);
            await deps.CommandHandler.ExecuteAsync(
                new SetVoiceDesignPromptCommand(_folder, id, voice.DesignPrompt), ct);
        }

        // No per-voice UI update: the voices are new, so the tab reloads on BatchCompleted.
        return new PhaseStepOutcome(Ok: true, Update: null, FailReason: null);
    }
}
