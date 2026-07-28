using Read2Me.App.State;
using Read2Me.Services.NodeStatus;
using Xunit;

namespace Read2Me.Tests.State
{
    public class NodeRowViewModelTests
    {
        private static NodeStatusSummary Status(
            int attribution = 0, int audio = 0, int review = 0,
            bool processing = false, int queued = 0) =>
            new(attribution, audio, review, processing, queued);

        [Fact]
        public void NotSelectable_HidesSelectionControls()
        {
            var vm = NodeRowViewModel.For(false, TriState.Unchecked, Status());
            Assert.False(vm.ShowSelectionControls);
        }

        [Theory]
        [InlineData(TriState.Checked, true)]
        [InlineData(TriState.Unchecked, false)]
        [InlineData(TriState.Indeterminate, null)]
        public void MapsTriStateToNullableBool(TriState state, bool? expected)
        {
            var vm = NodeRowViewModel.For(true, state, Status());
            Assert.Equal(expected, vm.CheckboxValue);
        }

        [Fact]
        public void Processing_ShowsProcessingChip_NotQueued()
        {
            var vm = NodeRowViewModel.For(true, TriState.Indeterminate, Status(processing: true));
            Assert.True(vm.ShowAttributionProcessingChip);
            Assert.False(vm.ShowAttributionQueuedChip);
        }

        [Fact]
        public void QueuedCountPositive_ShowsQueuedChip_WithCount()
        {
            var vm = NodeRowViewModel.For(true, TriState.Unchecked, Status(queued: 3));
            Assert.True(vm.ShowAttributionQueuedChip);
            Assert.Equal(3, vm.AttributionQueuedCount);
        }

        [Fact]
        public void ProcessingAndQueued_ShowsBothChips()
        {
            var vm = NodeRowViewModel.For(true, TriState.Indeterminate, Status(processing: true, queued: 2));
            Assert.True(vm.ShowAttributionProcessingChip);
            Assert.True(vm.ShowAttributionQueuedChip);
        }

        [Fact]
        public void AttributionRemaining_NonZero_ShowsAttributionBadge()
        {
            var vm = NodeRowViewModel.For(true, TriState.Unchecked, Status(attribution: 3));
            Assert.True(vm.ShowAttributionBadge);
            Assert.Equal(3, vm.AttributionRemaining);
        }

        [Fact]
        public void AttributionRemaining_Zero_HidesAttributionBadge()
        {
            var vm = NodeRowViewModel.For(true, TriState.Unchecked, Status());
            Assert.False(vm.ShowAttributionBadge);
            Assert.Equal(0, vm.AttributionRemaining);
        }

        [Fact]
        public void AudioRemaining_NonZero_ShowsAudioBadge()
        {
            var vm = NodeRowViewModel.For(true, TriState.Unchecked, Status(audio: 4));
            Assert.True(vm.ShowAudioBadge);
            Assert.Equal(4, vm.AudioRemaining);
        }

        [Fact]
        public void AudioRemaining_Zero_HidesAudioBadge()
        {
            var vm = NodeRowViewModel.For(true, TriState.Unchecked, Status());
            Assert.False(vm.ShowAudioBadge);
            Assert.Equal(0, vm.AudioRemaining);
        }

        [Fact]
        public void ReviewRemaining_NonZero_ShowsReviewBadge()
        {
            var vm = NodeRowViewModel.For(true, TriState.Unchecked, Status(review: 2));
            Assert.True(vm.ShowReviewBadge);
            Assert.Equal(2, vm.Review);
            Assert.False(vm.ShowDoneIndicator);
        }

        [Fact]
        public void AllStagesZero_ShowsDoneIndicator_HidesAllBadges()
        {
            var vm = NodeRowViewModel.For(true, TriState.Unchecked, Status());
            Assert.False(vm.ShowAttributionBadge);
            Assert.False(vm.ShowAudioBadge);
            Assert.False(vm.ShowReviewBadge);
            Assert.True(vm.ShowDoneIndicator);
        }

        [Fact]
        public void AttributionNonZero_HidesDoneIndicator()
        {
            var vm = NodeRowViewModel.For(true, TriState.Unchecked, Status(attribution: 1));
            Assert.False(vm.ShowDoneIndicator);
        }

        [Fact]
        public void DoneIndicator_IgnoresInFlightWork()
        {
            // Done means no work remains, not that nothing is running.
            var vm = NodeRowViewModel.For(true, TriState.Unchecked, Status(processing: true, queued: 4));
            Assert.True(vm.ShowDoneIndicator);
        }
    }
}
