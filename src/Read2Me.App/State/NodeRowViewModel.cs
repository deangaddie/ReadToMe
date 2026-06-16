using Read2Me.Services.Characters;

namespace Read2Me.App.State
{
    public readonly struct NodeRowViewModel
    {
        public bool ShowSelectionControls { get; }
        public bool? CheckboxValue { get; }
        public bool ShowProcessingChip { get; }
        public bool ShowQueuedChip { get; }
        public int QueuedCount { get; }

        private NodeRowViewModel(
            bool showSelectionControls, bool? checkboxValue,
            bool showProcessingChip, bool showQueuedChip, int queuedCount)
        {
            ShowSelectionControls = showSelectionControls;
            CheckboxValue = checkboxValue;
            ShowProcessingChip = showProcessingChip;
            ShowQueuedChip = showQueuedChip;
            QueuedCount = queuedCount;
        }

        public static NodeRowViewModel For(bool isSelectable, TriState state, NodeQueueSummary queue) =>
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
                queuedCount: queue.QueuedCount);
    }
}
