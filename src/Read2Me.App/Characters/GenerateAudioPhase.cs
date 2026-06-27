using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Read2Me.Core.Models;
using Read2Me.Data.Enums;
using Read2Me.Services;
using Read2Me.Services.Audio.VoiceDesign;

namespace Read2Me.App.Characters;

public sealed class GenerateAudioPhase : ISweepPhase<AudioWorkItem>
{
    private ProjectFolderId _folder;

    public string Operation => "Generating audio";

    public string DisplayName(AudioWorkItem item) => item.CharacterName;

    public async Task<IReadOnlyList<AudioWorkItem>> PlanAsync(
        PhaseDeps deps, ProjectFolderId folder, CancellationToken ct)
    {
        _folder = folder;
        var characters = await deps.Reader.GetCharactersWithAliasesAsync(folder);

        var workList = new List<AudioWorkItem>();
        foreach (var character in characters)
        {
            ct.ThrowIfCancellationRequested();
            var voices = await deps.Reader.GetCharacterVoicesAsync(folder, character.Id);
            foreach (var voice in voices)
            {
                if (voice.Source == VoiceSource.Generated
                    && !string.IsNullOrWhiteSpace(voice.DesignPrompt)
                    && string.IsNullOrWhiteSpace(voice.AudioFileName))
                {
                    workList.Add(new AudioWorkItem(
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
        return workList;
    }

    public async Task<PhaseStepOutcome> RunStepAsync(
        AudioWorkItem item, PhaseDeps deps, CancellationToken ct)
    {
        var request = new VoiceGenerationRequest
        {
            FolderId = _folder,
            CharacterId = item.CharacterId,
            CharacterName = item.CharacterName,
            CharacterAliases = item.Aliases,
            VoiceId = item.VoiceId,
            VoiceName = item.VoiceName,
            DesignPrompt = item.DesignPrompt,
            SettingsOverrideJson = item.SettingsOverrideJson,
        };

        var result = await deps.Orchestrator.GenerateVoiceAudioAsync(request, ct);

        if (result.IsSuccess)
        {
            return new PhaseStepOutcome(
                Ok: true,
                Update: new VoiceUpdated(item.CharacterId, item.VoiceId, null, result.AudioFileName, result.Transcript),
                FailReason: null);
        }

        return new PhaseStepOutcome(Ok: false, Update: null, FailReason: result.ErrorMessage);
    }
}
