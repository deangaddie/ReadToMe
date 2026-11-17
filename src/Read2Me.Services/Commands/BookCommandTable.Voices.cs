using Read2Me.Core.Models;
using Read2Me.Services.Commands.Handlers;
using Read2Me.Services.History;
using Read2Me.Services.Mutations;
using static Read2Me.Services.Mutations.BookMutationRefusalKind;

namespace Read2Me.Services.Commands;

// Voices and voice rules.
//
// Every one of these has always answered a gesture aimed at a Voice, rule or Character the project does
// not contain with 200 { "newEntityId": null }, so missing stays flattened. CreateVoiceRule,
// DeleteVoiceRule and MoveVoiceRule answer invalid as null too: a rule pointed at another Character's
// Voice, and deleting or reordering the default rule, were silent no-ops before ADR 0007 gave them a
// refusal, and the wire contract did not move with it.
//
// SetVoiceSource and DeleteVoice take a Voice's recording away, so they cross BookMutations through
// IVoiceAudioRemover rather than directly: the file has to go after the commit that stops naming it.
public static partial class BookCommandTable
{
    private static Row[] Voices =>
    [
        Mutation<CreateVoiceCommand>(
            c => new CreateVoiceMutation(c.FolderId, c.CharacterId, c.Name, c.IsGenerated), "New voice", StepAction.Voice),
        Mutation<UpdateVoiceCommand>(
            c => new UpdateVoiceMutation(c.FolderId, c.VoiceId, c.Name, c.Description), "Edit voice", StepAction.Voice),
        Handler<DeleteVoiceCommand, DeleteVoiceHandler>("Delete voice", StepAction.Delete),
        Mutation<SetVoiceDefaultCommand>(
            c => new SetVoiceDefaultMutation(c.FolderId, c.VoiceId), "Set default voice", StepAction.Voice),
        Mutation<SetVoiceDesignPromptCommand>(
            c => new SetVoiceDesignPromptMutation(c.FolderId, c.VoiceId, c.Prompt), "Edit design prompt", StepAction.Voice),
        Mutation<SetVoiceSettingsOverrideCommand>(
            c => new SetVoiceDesignSettingsOverrideMutation(c.FolderId, c.VoiceId, c.Json),
            "Change voice settings", StepAction.Voice),
        Mutation<SetVoiceTtsSettingsOverrideCommand>(
            c => new SetVoiceTtsSettingsOverrideMutation(c.FolderId, c.VoiceId, c.Json),
            "Change voice settings", StepAction.Voice),
        Mutation<SetVoiceTranscriptCommand>(
            c => new SetVoiceTranscriptMutation(c.FolderId, c.VoiceId, c.Transcript), "Edit transcript", StepAction.Voice),
        Mutation<SetVoiceAudioCommand>(
            c => new SetVoiceAudioMutation(c.FolderId, c.VoiceId, c.AudioFileName), "Set voice audio", StepAction.Voice),
        Mutation<SetVoiceGeneratedCommand>(
            c => new SetVoiceGeneratedMutation(c.FolderId, c.VoiceId, c.AudioFileName, c.Transcript, c.DesignPrompt),
            "Generate audio", StepAction.Voice),
        Handler<SetVoiceSourceCommand, SetVoiceSourceHandler>("Change voice source", StepAction.Voice),

        Mutation<CreateVoiceRuleCommand>(
            c => new CreateVoiceRuleMutation(
                c.FolderId, c.CharacterId, c.VoiceId, c.FromLevel, c.FromNodeId, c.ToLevel, c.ToNodeId),
            "Add voice rule", StepAction.Voice, answersNullFor: [Missing, Invalid]),
        Mutation<DeleteVoiceRuleCommand>(
            c => new DeleteVoiceRuleMutation(c.FolderId, c.RuleId), "Delete voice rule", StepAction.Delete,
            answersNullFor: [Missing, Invalid]),
        Mutation<MoveVoiceRuleCommand>(
            c => new MoveVoiceRuleMutation(c.FolderId, c.RuleId, c.Direction), "Move voice rule", StepAction.Voice,
            answersNullFor: [Missing, Invalid]),
    ];
}
