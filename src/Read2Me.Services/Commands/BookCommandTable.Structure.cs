using Read2Me.Core.Models;
using Read2Me.Services.Commands.Handlers;
using Read2Me.Services.History;
using Read2Me.Services.Mutations;

namespace Read2Me.Services.Commands;

// Structure: insert, split, merge, delete, titles, pauses, clear.
//
// A delete, merge or split aimed at a node the Book does not contain answers with no id rather than
// failing, as it always has. A merge never created anything, so it answers with no id, and a first or
// last sibling with nothing to merge into is a quiet no-op. A title or pause sweep that finds nothing
// to add says so (NoChange) rather than committing an empty transaction.
//
// InsertNode is strict: an agent scripting a structural repair is told about an unknown anchor rather
// than handed a quiet null it could mistake for a no-op.
//
// InsertPauseParagraph never reports its id. The mutation names the Paragraph it created, because a
// reader reconciling from the receipt needs it, but the command has never answered with one and the
// wire contract stays fixed (ADR 0007), so its handler drops the identity.
public static partial class BookCommandTable
{
    private static Row[] Structure =>
    [
        Mutation<InsertNodeCommand>(
            c => new InsertNodeMutation(c.FolderId, c.AnchorKind, c.AnchorId, c.Placement, c.Title, c.Text),
            "Insert", StepAction.Insert, answersNullFor: []),
        Mutation<InsertParagraphItemCommand>(
            c => new InsertParagraphItemMutation(c.FolderId, c.AnchorItemId, c.Position, c.Text),
            "Insert text", StepAction.Insert),
        Handler<InsertPauseParagraphCommand, InsertPauseParagraphHandler>("Insert pause", StepAction.Pause),

        Mutation<SplitAtPartCommand>(
            c => new SplitAtPartMutation(c.FolderId, c.PartId, c.NewVolumeTitle), "Split volume", StepAction.Split),
        Mutation<SplitAtChapterCommand>(
            c => new SplitAtChapterMutation(c.FolderId, c.ChapterId, c.NewPartTitle), "Split part", StepAction.Split),
        Mutation<SplitAtParagraphCommand>(
            c => new SplitAtParagraphMutation(c.FolderId, c.ParagraphId, c.NewChapterTitle), "Split chapter", StepAction.Split),
        Mutation<SplitAtItemCommand>(
            c => new SplitAtItemMutation(c.FolderId, c.ItemId), "Split paragraph", StepAction.Split),

        Mutation<MergeVolumeCommand>(
            c => new MergeVolumeMutation(c.FolderId, c.VolumeId, c.Direction), "Merge volumes", StepAction.Merge),
        Mutation<MergePartCommand>(
            c => new MergePartMutation(c.FolderId, c.PartId, c.Direction), "Merge parts", StepAction.Merge),
        Mutation<MergeChapterCommand>(
            c => new MergeChapterMutation(c.FolderId, c.ChapterId, c.Direction), "Merge chapters", StepAction.Merge),
        Mutation<MergeParagraphCommand>(
            c => new MergeParagraphMutation(c.FolderId, c.ParagraphId, c.Direction), "Merge paragraphs", StepAction.Merge),
        Mutation<MergeParagraphItemCommand>(
            c => new MergeParagraphItemMutation(c.FolderId, c.ItemId, c.Direction), "Merge text", StepAction.Merge),

        Mutation<DeleteVolumeCommand>(
            c => new DeleteVolumeMutation(c.FolderId, c.VolumeId), "Delete volume", StepAction.Delete),
        Mutation<DeletePartCommand>(
            c => new DeletePartMutation(c.FolderId, c.PartId), "Delete part", StepAction.Delete),
        Mutation<DeleteChapterCommand>(
            c => new DeleteChapterMutation(c.FolderId, c.ChapterId), "Delete chapter", StepAction.Delete),
        Mutation<DeleteParagraphCommand>(
            c => new DeleteParagraphMutation(c.FolderId, c.ParagraphId), "Delete paragraph", StepAction.Delete),
        Mutation<DeleteParagraphItemCommand>(
            c => new DeleteParagraphItemMutation(c.FolderId, c.ItemId), "Delete text", StepAction.Delete),
        Mutation<ClearBookContentCommand>(
            c => new ClearBookContentMutation(c.FolderId), "Clear book", StepAction.Delete),

        Handler<AddTitlesCommand, AddTitlesHandler>("Add titles", StepAction.Title),
        Mutation<AddBookTitleCommand>(c => new AddBookTitleMutation(c.FolderId), "Add titles", StepAction.Title),
        Mutation<AddVolumeTitlesCommand>(c => new AddVolumeTitlesMutation(c.FolderId), "Add titles", StepAction.Title),
        Mutation<AddPartTitlesCommand>(c => new AddPartTitlesMutation(c.FolderId), "Add titles", StepAction.Title),
        Mutation<AddChapterTitlesCommand>(c => new AddChapterTitlesMutation(c.FolderId), "Add titles", StepAction.Title),
        Mutation<AddPausesCommand>(c => new AddPausesMutation(c.FolderId), "Add pauses", StepAction.Pause),
    ];
}
