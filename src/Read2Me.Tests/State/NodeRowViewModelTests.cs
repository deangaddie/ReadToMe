using Read2Me.App.State;
using Read2Me.Services.Characters;
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
    }
}
