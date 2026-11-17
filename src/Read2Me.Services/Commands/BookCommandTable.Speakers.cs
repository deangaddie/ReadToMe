using Read2Me.Core.Models;
using Read2Me.Services.Commands.Handlers;
using Read2Me.Services.History;
using Read2Me.Services.Mutations;
using static Read2Me.Services.Mutations.BookMutationRefusalKind;

namespace Read2Me.Services.Commands;

// Speakers: speaker assignment, the Character roster, the narrator, attribution.
//
// The commands say "character" and the speaker mutations say "speaker" because the wire names are
// fixed and the domain's word is not: narration is a speaker too (ADR 0006), so a gesture that can
// stamp the narrator is not a "set character". The two vocabularies meet in these rows.
//
// RenameCharacter, MergeCharacters and DeleteCharacter answer invalid as null as well as missing. Their
// mutations can refuse a protected-narrator gesture, and the endpoint has always answered those with
// 200 { "newEntityId": null }; the mutations now say why they refused, and this is where that added
// precision is flattened back to the contract agents already hold.
//
// SetNarratorCharacter is strict. Answering null would render a refusal to an agent as
// 200 { "newEntityId": null }, indistinguishable from success, including for a link to a character this
// project does not have, so missing is not flattened either. The agent API and the book banner both
// land on it, so human and machine paths cannot drift (ADR 0004).
//
// AttributeItems is the Character Queue's answer. One whose stamps all agree with what the items
// already carry is NoChange and consumes no revision; the response stays null either way.
public static partial class BookCommandTable
{
    private static Row[] Speakers =>
    [
        Mutation<SetItemCharacterCommand>(
            c => new SetItemSpeakerMutation(c.FolderId, c.ItemId, c.CharacterId), "Assign speaker", StepAction.Character),
        Mutation<SetParagraphCharacterCommand>(
            c => new SetParagraphSpeakerMutation(c.FolderId, c.ParagraphId, c.CharacterId, c.VoiceInstructions),
            "Assign speaker", StepAction.Character),
        Mutation<SetParagraphsCharacterCommand>(
            c => new SetParagraphsSpeakerMutation(c.FolderId, c.ParagraphIds, c.CharacterId),
            "Assign speaker", StepAction.Character),
        Mutation<AttributeItemsCommand>(
            c => new AttributeParagraphItemsMutation(c.FolderId, c.ParagraphId, c.Items),
            "Assign speakers", StepAction.Character),
        Mutation<ReleaseNarrationCommand>(
            c => new ReleaseNarrationMutation(c.FolderId, c.Scope, c.NodeId), "Release narration", StepAction.Character),

        Handler<CreateCharacterCommand, CreateCharacterHandler>("New character", StepAction.Character),
        Handler<CreateCharacterAndAssignCommand, CreateCharacterAndAssignHandler>("New character", StepAction.Character),
        Mutation<RenameCharacterCommand>(
            c => new RenameCharacterMutation(c.FolderId, c.CharacterId, c.Name), "Rename character", StepAction.Character,
            answersNullFor: [Missing, Invalid]),
        Mutation<AddCharacterAliasCommand>(
            c => new AddCharacterAliasMutation(c.FolderId, c.CharacterId, c.Name), "Add alias", StepAction.Character),
        Mutation<RemoveCharacterAliasCommand>(
            c => new RemoveCharacterAliasMutation(c.FolderId, c.AliasId), "Remove alias", StepAction.Delete),
        Mutation<MergeCharactersCommand>(
            c => new MergeCharactersMutation(c.FolderId, c.SurvivorId, c.MergedId, c.AddNameAsAlias),
            "Merge characters", StepAction.Merge, answersNullFor: [Missing, Invalid]),
        Mutation<DeleteCharacterCommand>(
            c => new DeleteCharacterMutation(c.FolderId, c.CharacterId), "Delete character", StepAction.Delete,
            answersNullFor: [Missing, Invalid]),
        Mutation<SetNarratorCharacterCommand>(
            c => new SetNarratorCharacterMutation(c.FolderId, c.CharacterId), "Choose narrator", StepAction.Character,
            answersNullFor: []),
    ];
}
