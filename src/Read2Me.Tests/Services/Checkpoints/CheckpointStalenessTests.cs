using Read2Me.Core.Models;
using Read2Me.Data.Enums;
using Read2Me.Services.Checkpoints;
using Read2Me.Services.Mutations;
using Xunit;

namespace Read2Me.Tests.Services.Checkpoints
{
    /// <summary>
    /// Which confirmed checkpoints a Book mutation disturbs. Keyed on the mutation type, not the
    /// facet: Add titles, Add pauses and paragraph inserts report Structure too, and must not stale
    /// the very steps they are how a user finishes. Every row of the spec's table is pinned here,
    /// including each mutation that never stales anything.
    /// </summary>
    public class CheckpointStalenessTests
    {
        private static readonly ProjectFolderId Folder = new("book");
        private static readonly Guid Id = Guid.NewGuid();

        private static readonly BookMutationEffects Structural =
            new() { Scope = BookMutationScope.WholeProject, Facets = BookFacets.Structure };

        private static readonly PipelineStep[] ChapterLevel =
            [PipelineStep.Structure, PipelineStep.Titles, PipelineStep.Pauses];

        public static TheoryData<BookMutation> ChapterLevelSet => new()
        {
            new SplitAtPartMutation(Folder, Id, null),
            new SplitAtChapterMutation(Folder, Id, null),
            new MergeVolumeMutation(Folder, Id, MergeDirection.Previous),
            new MergePartMutation(Folder, Id, MergeDirection.Previous),
            new MergeChapterMutation(Folder, Id, MergeDirection.Next),
            new DeleteVolumeMutation(Folder, Id),
            new DeletePartMutation(Folder, Id),
            new DeleteChapterMutation(Folder, Id),
        };

        [Theory]
        [MemberData(nameof(ChapterLevelSet))]
        public void ChapterLevelMutation_stalesStructureTitlesAndPauses(BookMutation mutation)
        {
            Assert.Equal(ChapterLevel, CheckpointStaleness.StepsToStale(mutation, Structural));
        }

        [Theory]
        [InlineData(NodeKind.Volume)]
        [InlineData(NodeKind.Part)]
        [InlineData(NodeKind.Chapter)]
        public void InsertingAContainerNode_stalesStructureTitlesAndPauses(NodeKind anchor)
        {
            var mutation = new InsertNodeMutation(Folder, anchor, Id, Placement.After, "Title", null);
            var createdId = Guid.NewGuid();
            var effects = new BookMutationEffects
            {
                Scope = BookMutationScope.Exact, Facets = BookFacets.Structure,
                CreatedId = createdId, NodeIds = [createdId],
            };

            Assert.Equal(ChapterLevel, CheckpointStaleness.StepsToStale(mutation, effects));
        }

        [Fact]
        public void InsertingAParagraph_stalesNothing()
        {
            var mutation = new InsertNodeMutation(Folder, NodeKind.Paragraph, Id, Placement.After, null, "Text");
            var createdId = Guid.NewGuid();
            var effects = new BookMutationEffects
            {
                Scope = BookMutationScope.Exact, Facets = BookFacets.Structure,
                CreatedId = createdId, ParagraphIds = [createdId],
            };

            Assert.Empty(CheckpointStaleness.StepsToStale(mutation, effects));
        }

        [Fact]
        public void InsertingAnItem_stalesNothing()
        {
            var mutation = new InsertNodeMutation(Folder, NodeKind.ParagraphItem, Id, Placement.After, null, "Text");
            var createdId = Guid.NewGuid();
            var effects = new BookMutationEffects
            {
                Scope = BookMutationScope.Exact, Facets = BookFacets.Structure,
                CreatedId = createdId, ParagraphIds = [Guid.NewGuid()], ParagraphItemIds = [createdId],
            };

            Assert.Empty(CheckpointStaleness.StepsToStale(mutation, effects));
        }

        public static TheoryData<BookMutation> TitleRenames => new()
        {
            new UpdateVolumeTitleMutation(Folder, Id, "New"),
            new UpdatePartTitleMutation(Folder, Id, "New"),
            new UpdateChapterTitleMutation(Folder, Id, "New"),
        };

        [Theory]
        [MemberData(nameof(TitleRenames))]
        public void RenamingANode_stalesTitles(BookMutation mutation)
        {
            var effects = new BookMutationEffects { Scope = BookMutationScope.Exact, Facets = BookFacets.NodeTitle };

            Assert.Equal([PipelineStep.Titles], CheckpointStaleness.StepsToStale(mutation, effects));
        }

        [Theory]
        [InlineData(BookFacets.ItemText | BookFacets.Audio, new[] { PipelineStep.Text })]
        [InlineData(BookFacets.NodeTitle, new[] { PipelineStep.Titles })]
        [InlineData(BookFacets.ItemText | BookFacets.NodeTitle, new[] { PipelineStep.Text, PipelineStep.Titles })]
        public void ApplyBookEdits_stalesWhatItReported(BookFacets facets, PipelineStep[] expected)
        {
            var mutation = new ApplyBookEditsMutation(Folder, []);
            var effects = new BookMutationEffects { Scope = BookMutationScope.Exact, Facets = facets };

            Assert.Equal(expected, CheckpointStaleness.StepsToStale(mutation, effects));
        }

        public static TheoryData<BookMutation> Never => new()
        {
            new SplitAtParagraphMutation(Folder, Id, null),
            new SplitAtItemMutation(Folder, Id),
            new MergeParagraphMutation(Folder, Id, MergeDirection.Previous),
            new MergeParagraphItemMutation(Folder, Id, MergeDirection.Previous),
            new InsertParagraphItemMutation(Folder, Id, InsertPosition.After, "Text"),
            new DeleteParagraphMutation(Folder, Id),
            new DeleteParagraphItemMutation(Folder, Id),
            new AddBookTitleMutation(Folder),
            new AddVolumeTitlesMutation(Folder),
            new AddPartTitlesMutation(Folder),
            new AddChapterTitlesMutation(Folder),
            new AddPausesMutation(Folder),
            new InsertPauseParagraphMutation(Folder, Id, InsertPosition.After, PauseKind.Pause),
            new UpdateParagraphItemTextMutation(Folder, Id, "Text"),
            new SetItemSpeakerMutation(Folder, Id, null),
            new SetCheckpointMutation(Folder, PipelineStep.Structure, CheckpointState.Confirmed),
        };

        // Facets are every one a mutation could report: the type alone decides, so none of these may
        // be talked into staling by what they changed.
        [Theory]
        [MemberData(nameof(Never))]
        public void OtherMutations_staleNothing(BookMutation mutation)
        {
            var effects = new BookMutationEffects { Scope = BookMutationScope.WholeProject, Facets = BookFacets.All };

            Assert.Empty(CheckpointStaleness.StepsToStale(mutation, effects));
        }
    }
}
