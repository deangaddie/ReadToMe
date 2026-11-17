using Read2Me.Core.Models;
using Read2Me.Services.History;
using Read2Me.Services.Mutations;

namespace Read2Me.Services.Commands;

// Text: item text, node titles, delivery cue, AI edits.
//
// A node the Book does not contain, and a value the node already carries, both answer null. A text
// rewrite discards the item's stale audio and any verdict on it (Mutations.Implementations.BookEditEffects).
// An AI edit program that resolved no target, or proposed only the wording already there, answers null.
//
// SetItemDeliveryCue is strict: an unknown item, a Pause or an over-long cue answers 422 rather than a
// success-shaped null.
public static partial class BookCommandTable
{
    private static Row[] Text =>
    [
        Mutation<UpdateParagraphItemTextCommand>(
            c => new UpdateParagraphItemTextMutation(c.FolderId, c.ItemId, c.Text), "Edit text", StepAction.Edit),
        Mutation<UpdateVolumeTitleCommand>(
            c => new UpdateVolumeTitleMutation(c.FolderId, c.VolumeId, c.Title), "Edit title", StepAction.Title),
        Mutation<UpdatePartTitleCommand>(
            c => new UpdatePartTitleMutation(c.FolderId, c.PartId, c.Title), "Edit title", StepAction.Title),
        Mutation<UpdateChapterTitleCommand>(
            c => new UpdateChapterTitleMutation(c.FolderId, c.ChapterId, c.Title), "Edit title", StepAction.Title),
        Mutation<ApplyBookEditsCommand>(
            c => new ApplyBookEditsMutation(c.FolderId, c.Edits), "Edit with AI", StepAction.EditWithAi),
        Mutation<SetItemDeliveryCueCommand>(
            c => new SetItemDeliveryCueMutation(c.FolderId, c.ItemId, c.Cue), "Edit delivery cue", StepAction.Edit,
            answersNullFor: []),
    ];
}
