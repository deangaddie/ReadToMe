using Read2Me.Services.Characters;
using Read2Me.Services.NodeStatus;

namespace Read2Me.App.State
{
    public readonly struct NodeRowViewModel
    {
        public bool ShowSelectionControls { get; }
        public bool? CheckboxValue { get; }
        public bool ShowProcessingChip { get; }
        public bool ShowQueuedChip { get; }
        public int QueuedCount { get; }
        public bool ShowAttributionBadge { get; }
        public int AttributionRemaining { get; }
        public bool ShowAudioBadge { get; }
        public int AudioRemaining { get; }
        public bool ShowReviewBadge { get; }
        public int Review { get; }
        public bool ShowDoneIndicator { get; }

        private NodeRowViewModel(
            bool showSelectionControls, bool? checkboxValue,
            bool showProcessingChip, bool showQueuedChip, int queuedCount,
            bool showAttributionBadge, int attributionRemaining,
            bool showAudioBadge, int audioRemaining,
            bool showReviewBadge, int review)
        {
            ShowSelectionControls = showSelectionControls;
            CheckboxValue = checkboxValue;
            ShowProcessingChip = showProcessingChip;
            ShowQueuedChip = showQueuedChip;
            QueuedCount = queuedCount;
            ShowAttributionBadge = showAttributionBadge;
            AttributionRemaining = attributionRemaining;
            ShowAudioBadge = showAudioBadge;
            AudioRemaining = audioRemaining;
            ShowReviewBadge = showReviewBadge;
            Review = review;
            ShowDoneIndicator = !showAttributionBadge && !showAudioBadge && !showReviewBadge;
        }

        public static NodeRowViewModel For(bool isSelectable, TriState state, NodeQueueSummary queue) =>
            For(isSelectable, state, queue, new NodeStatusSummary(0, 0, 0));

        public static NodeRowViewModel For(bool isSelectable, TriState state, NodeQueueSummary queue, NodeStatusSummary status) =>
            new(
                showSelectionControls: isSelectable,
                checkboxValue: state switch
                {
                    TriState.Checked => true,
                    TriState.Unchecked => false,
                    _ => null
                },
                showProcessingChip: queue.HasProcessing,
                showQueuedChip: queue.QueuedCount > 0,
                queuedCount: queue.QueuedCount,
                showAttributionBadge: status.AttributionRemaining > 0,
                attributionRemaining: status.AttributionRemaining,
                showAudioBadge: status.AudioRemaining > 0,
                audioRemaining: status.AudioRemaining,
                showReviewBadge: status.Review > 0,
                review: status.Review);
    }
}
