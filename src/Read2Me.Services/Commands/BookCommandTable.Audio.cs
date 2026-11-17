using Read2Me.Core.Models;
using Read2Me.Services.History;
using Read2Me.Services.Mutations;

namespace Read2Me.Services.Commands;

// Audio: item audio and audio review. Results only, so these never record a History step.
//
// The Audio Queue does not come through SetParagraphItemAudio (it records the take and its verdict
// together), so an unknown item and a path the item already carries both answer null. A review that
// changed nothing and an item the Book does not contain answer null too.
public static partial class BookCommandTable
{
    private static Row[] Audio =>
    [
        Mutation<SetParagraphItemAudioCommand>(
            c => new SetParagraphItemAudioMutation(c.FolderId, c.ItemId, c.AudioFileName), "Item audio", StepAction.Edit),
        Mutation<SetAudioReviewCommand>(
            c => new SetAudioReviewMutation(c.FolderId, c.ParagraphItemId, new AudioReviewVerdict(
                c.NormalizeOk, c.NormalizeReason,
                c.VerifyOk, c.Wer, c.VerifyReason,
                c.Transcript, c.OriginalTextSnapshot)),
            "Audio review", StepAction.Edit),
        Mutation<DismissAudioReviewCommand>(
            c => new DismissAudioReviewMutation(c.FolderId, c.ParagraphItemId), "Dismiss audio review", StepAction.Edit),
    ];
}
