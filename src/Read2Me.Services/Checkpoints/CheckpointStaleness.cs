using Read2Me.Data.Enums;
using Read2Me.Services.Mutations;

namespace Read2Me.Services.Checkpoints;

/// <summary>
/// Which confirmed checkpoints a committed Book mutation disturbs, so the step asks to be
/// reconfirmed. Keyed on the mutation's type, not its facets: Add titles, Add pauses and paragraph
/// inserts all report <see cref="BookFacets.Structure"/>, and they are how a user finishes the very
/// steps a facet rule would stale. Speakers is never staled; a reread deletes every row instead.
/// </summary>
public static class CheckpointStaleness
{
    private static readonly PipelineStep[] ChapterLevel =
        [PipelineStep.Structure, PipelineStep.Titles, PipelineStep.Pauses];

    /// <summary>The steps to mark stale, in Pipeline order; empty when the mutation disturbs none.</summary>
    public static IReadOnlyList<PipelineStep> StepsToStale(BookMutation mutation, BookMutationEffects effects) =>
        mutation switch
        {
            SplitAtPartMutation or SplitAtChapterMutation
                or MergeVolumeMutation or MergePartMutation or MergeChapterMutation
                or DeleteVolumeMutation or DeletePartMutation or DeleteChapterMutation => ChapterLevel,
            // A Volume, Part or Chapter insert reports the empty node it made in NodeIds; a Paragraph
            // or item insert reports it as a Paragraph and disturbs nothing confirmed.
            InsertNodeMutation when effects.CreatedId is { } created && effects.NodeIds.Contains(created) => ChapterLevel,
            UpdateVolumeTitleMutation or UpdatePartTitleMutation or UpdateChapterTitleMutation => [PipelineStep.Titles],
            // Everything an approved edit program commits counts as AI, hand-tuned review rows included.
            ApplyBookEditsMutation => BookEditSteps(effects.Facets),
            _ => [],
        };

    private static PipelineStep[] BookEditSteps(BookFacets facets) =>
    [
        .. facets.HasFlag(BookFacets.ItemText) ? [PipelineStep.Text] : Array.Empty<PipelineStep>(),
        .. facets.HasFlag(BookFacets.NodeTitle) ? [PipelineStep.Titles] : Array.Empty<PipelineStep>(),
    ];
}
