using Read2Me.Services.NodeStatus;

namespace Read2Me.App.State
{
    public readonly struct NodeRowViewModel
    {
        public bool ShowSelectionControls { get; }
        public bool? CheckboxValue { get; }
        public bool ShowAttributionProcessingChip { get; }
        public bool ShowAttributionQueuedChip { get; }
        public int AttributionQueuedCount { get; }
        public bool ShowAttributionBadge { get; }
        public int AttributionRemaining { get; }
        public bool ShowAudioBadge { get; }
        public int AudioRemaining { get; }
        public bool ShowReviewBadge { get; }
        public int Review { get; }
        public bool ShowDoneIndicator { get; }

        private NodeRowViewModel(
            bool showSelectionControls, bool? checkboxValue,
            bool showAttributionProcessingChip, bool showAttributionQueuedChip, int attributionQueuedCount,
            bool showAttributionBadge, int attributionRemaining,
            bool showAudioBadge, int audioRemaining,
            bool showReviewBadge, int review,
            bool showDoneIndicator)
        {
            ShowSelectionControls = showSelectionControls;
            CheckboxValue = checkboxValue;
            ShowAttributionProcessingChip = showAttributionProcessingChip;
            ShowAttributionQueuedChip = showAttributionQueuedChip;
            AttributionQueuedCount = attributionQueuedCount;
            ShowAttributionBadge = showAttributionBadge;
            AttributionRemaining = attributionRemaining;
            ShowAudioBadge = showAudioBadge;
            AudioRemaining = audioRemaining;
            ShowReviewBadge = showReviewBadge;
            Review = review;
            ShowDoneIndicator = showDoneIndicator;
        }

        public static NodeRowViewModel For(bool isSelectable, TriState state, NodeStatusSummary status) =>
            new(
                showSelectionControls: isSelectable,
                checkboxValue: state switch
                {
                    TriState.Checked => true,
                    TriState.Unchecked => false,
                    _ => null
                },
                showAttributionProcessingChip: status.AttributionProcessing,
                showAttributionQueuedChip: status.AttributionQueued > 0,
                attributionQueuedCount: status.AttributionQueued,
                showAttributionBadge: status.AttributionRemaining > 0,
                attributionRemaining: status.AttributionRemaining,
                showAudioBadge: status.AudioRemaining > 0,
                audioRemaining: status.AudioRemaining,
                showReviewBadge: status.Review > 0,
                review: status.Review,
                showDoneIndicator: status.IsDone);
    }
}
