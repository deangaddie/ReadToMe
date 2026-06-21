using Read2Me.App.State;
using Read2Me.Services.Characters;
using Read2Me.Services.NodeStatus;
using Xunit;

namespace Read2Me.Tests.State
{
    public class NodeRowViewModelTests
    {
        [Fact]
        public void NotSelectable_HidesSelectionControls()
        {
            var vm = NodeRowViewModel.For(false, TriState.Unchecked, new NodeQueueSummary(false, 0));
            Assert.False(vm.ShowSelectionControls);
        }

        [Theory]
        [InlineData(TriState.Checked, true)]
        [InlineData(TriState.Unchecked, false)]
        [InlineData(TriState.Indeterminate, null)]
        public void MapsTriStateToNullableBool(TriState state, bool? expected)
        {
            var vm = NodeRowViewModel.For(true, state, new NodeQueueSummary(false, 0));
            Assert.Equal(expected, vm.CheckboxValue);
        }

        [Fact]
        public void Processing_ShowsProcessingChip_NotQueued()
        {
            var vm = NodeRowViewModel.For(true, TriState.Indeterminate, new NodeQueueSummary(true, 0));
            Assert.True(vm.ShowProcessingChip);
            Assert.False(vm.ShowQueuedChip);
        }

        [Fact]
        public void QueuedCountPositive_ShowsQueuedChip_WithCount()
        {
            var vm = NodeRowViewModel.For(true, TriState.Unchecked, new NodeQueueSummary(false, 3));
            Assert.True(vm.ShowQueuedChip);
            Assert.Equal(3, vm.QueuedCount);
        }

        [Fact]
        public void ProcessingAndQueued_ShowsBothChips()
        {
            var vm = NodeRowViewModel.For(true, TriState.Indeterminate, new NodeQueueSummary(true, 2));
            Assert.True(vm.ShowProcessingChip);
            Assert.True(vm.ShowQueuedChip);
        }

        [Fact]
        public void AttributionRemaining_NonZero_ShowsAttributionBadge()
        {
            var vm = NodeRowViewModel.For(true, TriState.Unchecked, new NodeQueueSummary(false, 0), new NodeStatusSummary(3, 0, 0));
            Assert.True(vm.ShowAttributionBadge);
            Assert.Equal(3, vm.AttributionRemaining);
        }

        [Fact]
        public void AttributionRemaining_Zero_HidesAttributionBadge()
        {
            var vm = NodeRowViewModel.For(true, TriState.Unchecked, new NodeQueueSummary(false, 0), new NodeStatusSummary(0, 0, 0));
            Assert.False(vm.ShowAttributionBadge);
        }

        [Fact]
        public void For_WithoutStatus_HidesAttributionBadge()
        {
            var vm = NodeRowViewModel.For(true, TriState.Unchecked, new NodeQueueSummary(false, 0));
            Assert.False(vm.ShowAttributionBadge);
            Assert.Equal(0, vm.AttributionRemaining);
        }

        [Fact]
        public void AudioRemaining_NonZero_ShowsAudioBadge()
        {
            var vm = NodeRowViewModel.For(true, TriState.Unchecked, new NodeQueueSummary(false, 0), new NodeStatusSummary(0, 4, 0));
            Assert.True(vm.ShowAudioBadge);
            Assert.Equal(4, vm.AudioRemaining);
        }

        [Fact]
        public void AudioRemaining_Zero_HidesAudioBadge()
        {
            var vm = NodeRowViewModel.For(true, TriState.Unchecked, new NodeQueueSummary(false, 0), new NodeStatusSummary(0, 0, 0));
            Assert.False(vm.ShowAudioBadge);
        }

        [Fact]
        public void For_WithoutStatus_HidesAudioBadge()
        {
            var vm = NodeRowViewModel.For(true, TriState.Unchecked, new NodeQueueSummary(false, 0));
            Assert.False(vm.ShowAudioBadge);
            Assert.Equal(0, vm.AudioRemaining);
        }

        [Fact]
        public void ReviewRemaining_NonZero_ShowsReviewBadge()
        {
            var vm = NodeRowViewModel.For(true, TriState.Unchecked, new NodeQueueSummary(false, 0), new NodeStatusSummary(0, 0, 2));
            Assert.True(vm.ShowReviewBadge);
            Assert.Equal(2, vm.Review);
            Assert.False(vm.ShowDoneIndicator);
        }

        [Fact]
        public void AllStagesZero_ShowsDoneIndicator_HidesAllBadges()
        {
            var vm = NodeRowViewModel.For(true, TriState.Unchecked, new NodeQueueSummary(false, 0), new NodeStatusSummary(0, 0, 0));
            Assert.False(vm.ShowAttributionBadge);
            Assert.False(vm.ShowAudioBadge);
            Assert.False(vm.ShowReviewBadge);
            Assert.True(vm.ShowDoneIndicator);
        }

        [Fact]
        public void AttributionNonZero_HidesDoneIndicator()
        {
            var vm = NodeRowViewModel.For(true, TriState.Unchecked, new NodeQueueSummary(false, 0), new NodeStatusSummary(1, 0, 0));
            Assert.False(vm.ShowDoneIndicator);
        }
    }
}
