using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Read2Me.Core.Models;
using Read2Me.Data.Enums;
using Read2Me.Services;

namespace Read2Me.App.Characters;

public sealed class GeneratePromptsPhase : ISweepPhase<PromptWorkItem>
{
    private ProjectFolderId _folder;
    private string _bookTitle = string.Empty;
    private string _author = string.Empty;

    public string Operation => "Generating prompts";

    public string DisplayName(PromptWorkItem item) => item.CharacterName;

    public async Task<IReadOnlyList<PromptWorkItem>> PlanAsync(
        PhaseDeps deps, ProjectFolderId folder, CancellationToken ct)
    {
        _folder = folder;
        var project = await deps.Reader.GetProjectAsync(folder);
        _bookTitle = project?.BookTitle ?? folder.Value;
        _author = project?.Author ?? string.Empty;

        var characters = await deps.Reader.GetCharactersWithAliasesAsync(folder);

        // Pre-step: ensure every character has at least one voice
        foreach (var character in characters)
        {
            ct.ThrowIfCancellationRequested();
            var voices = await deps.Reader.GetCharacterVoicesAsync(folder, character.Id);
            if (voices.Count == 0)
            {
                await deps.CommandHandler.ExecuteAsync(
                    new CreateVoiceCommand(folder, character.Id, "Default", IsGenerated: true), ct);
            }
        }

        // Re-read and collect blank Generated voices
        var workList = new List<PromptWorkItem>();
        foreach (var character in characters)
        {
            ct.ThrowIfCancellationRequested();
            var voices = await deps.Reader.GetCharacterVoicesAsync(folder, character.Id);
            foreach (var voice in voices)
            {
                if (voice.Source == VoiceSource.Generated && string.IsNullOrWhiteSpace(voice.DesignPrompt))
                    workList.Add(new PromptWorkItem(character.Id, voice.Id, character.Name));
            }
        }
        return workList;
    }

    public async Task<PhaseStepOutcome> RunStepAsync(
        PromptWorkItem item, PhaseDeps deps, CancellationToken ct)
    {
        var renderedPrompt = await deps.Orchestrator.BuildRenderedPromptAsync(_bookTitle, _author, item.CharacterName);
        var designPrompt = await deps.Orchestrator.GenerateWithPromptAsync(renderedPrompt, ct);

        await deps.CommandHandler.ExecuteAsync(
            new SetVoiceDesignPromptCommand(_folder, item.VoiceId, designPrompt), ct);

        return new PhaseStepOutcome(
            Ok: true,
            Update: new VoiceUpdated(item.CharacterId, item.VoiceId, designPrompt, null, null),
            FailReason: null);
    }
}
