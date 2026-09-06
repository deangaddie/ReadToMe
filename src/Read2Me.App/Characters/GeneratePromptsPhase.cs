using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Read2Me.Core.Models;
using Read2Me.Data;
using Read2Me.Services;
using Read2Me.Services.Mutations;

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
            if (narrator.IsLinked && character.Id == ProjectDbContext.NarratorId)
                continue;

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
                // Through the writer, not the mutation: the voice's audio has to go after the commit
                // that stops naming it, never inside it.
                var dropped = await deps.VoiceAudio.DeleteVoiceAsync(_folder, voice.Id, ct);
                if (Refusal(dropped) is { } reason)
                    return new PhaseStepOutcome(Ok: false, Update: null, FailReason: reason);
            }
        }

        var plan = await deps.Orchestrator.GenerateVoicePlanAsync(
            _bookTitle, _author, item.CharacterName, item.IsNarrator, item.AlsoNarrates, ct);

        foreach (var voice in plan)
        {
            ct.ThrowIfCancellationRequested();

            // One commit per planned voice, not three: the name, description and design prompt are
            // the same fact about the same new voice, and splitting them would have every open Book
            // View reresolve its previews twice for a voice that is not finished being described.
            var created = await deps.Mutations.CommitAsync(
                new CreateVoiceMutation(
                    _folder, item.CharacterId, voice.Name,
                    IsGenerated: true, voice.Description, voice.DesignPrompt), ct);

            if (created is BookMutationOutcome.Rejected { Reason: BookMutationRejection.NotFound })
                return new PhaseStepOutcome(Ok: false, Update: null,
                    FailReason: $"Character {item.CharacterName} no longer exists");
            if (Refusal(created) is { } reason)
                return new PhaseStepOutcome(Ok: false, Update: null, FailReason: reason);
        }

        // No per-voice UI update: the voices are new, so the tab reloads on BatchCompleted.
        return new PhaseStepOutcome(Ok: true, Update: null, FailReason: null);
    }

    /// <summary>
    /// Why a step should stop, or null for an outcome it can carry on from.
    /// <para>
    /// A cancelled write is not one of these. The runner drops a cancelled sweep rather than counting
    /// a failure, so cancellation leaves by the exception every other cancellation point here throws
    /// — which is why that case is raised before the refusals are read rather than returned as one.
    /// </para>
    /// </summary>
    private static string? Refusal(BookMutationOutcome outcome)
    {
        if (outcome is BookMutationOutcome.Rejected { Reason: BookMutationRejection.Cancelled } cancelled)
            throw new OperationCanceledException(cancelled.Message);

        return outcome is BookMutationOutcome.Rejected rejected ? rejected.Message : null;
    }
}
