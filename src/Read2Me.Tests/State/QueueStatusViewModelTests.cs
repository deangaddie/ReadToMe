using Read2Me.App.State;
using Xunit;

namespace Read2Me.Tests.State
{
    public class QueueStatusViewModelTests
    {
        private static QueueStatusViewModel Vm(
            int queued = 0, int processing = 0, double avg = 0,
            double eta = 0, int completed = 0, double elapsed = 0,
            string unit = "item") =>
            QueueStatusViewModel.For(queued, processing, avg, eta, completed, elapsed, unit);

        [Fact]
        public void EmptyQueue_IsNotActive()
        {
            var vm = Vm();
            Assert.False(vm.IsActive);
        }

        [Theory]
        [InlineData(1, 0)]
        [InlineData(0, 1)]
        [InlineData(2, 3)]
        public void QueuedOrProcessing_IsActive(int queued, int processing)
        {
            Assert.True(Vm(queued: queued, processing: processing).IsActive);
        }

        [Fact]
        public void NotProcessing_HidesElapsed()
        {
            var vm = Vm(queued: 5, processing: 0, elapsed: 12);
            Assert.False(vm.ShowProcessing);
            Assert.Equal(string.Empty, vm.ElapsedText);
        }

        [Fact]
        public void Processing_FormatsElapsedAsWholeSeconds()
        {
            var vm = Vm(processing: 1, elapsed: 12.7);
            Assert.True(vm.ShowProcessing);
            Assert.Equal("13s", vm.ElapsedText);
        }

        [Fact]
        public void Queued_FormatsCount()
        {
            var vm = Vm(queued: 5, processing: 1);
            Assert.True(vm.ShowQueued);
            Assert.Equal("5 queued", vm.QueuedText);
        }

        [Fact]
        public void ZeroQueued_Hidden()
        {
            var vm = Vm(processing: 1);
            Assert.False(vm.ShowQueued);
        }

        [Theory]
        [InlineData("item", "avg 3.2s/item")]
        [InlineData("para", "avg 3.2s/para")]
        public void Average_FormatsWithUnit(string unit, string expected)
        {
            var vm = Vm(processing: 1, avg: 3.2, unit: unit);
            Assert.True(vm.ShowAverage);
            Assert.Equal(expected, vm.AverageText);
        }

        [Fact]
        public void ZeroAverage_Hidden()
        {
            var vm = Vm(processing: 1, avg: 0);
            Assert.False(vm.ShowAverage);
            Assert.Equal(string.Empty, vm.AverageText);
        }

        [Fact]
        public void Eta_SubHour_FormatsMinutesSeconds()
        {
            var vm = Vm(processing: 1, eta: 80); // 1m 20s
            Assert.True(vm.ShowEta);
            Assert.Equal("ETA 1m 20s", vm.EtaText);
        }

        [Fact]
        public void Eta_OverHour_FormatsHoursMinutes()
        {
            var vm = Vm(processing: 1, eta: 3 * 3600 + 5 * 60); // 3h 05m
            Assert.True(vm.ShowEta);
            Assert.Equal("ETA 3h 05m", vm.EtaText);
        }

        [Fact]
        public void ZeroEta_Hidden()
        {
            var vm = Vm(processing: 1, eta: 0);
            Assert.False(vm.ShowEta);
        }

        [Fact]
        public void Completed_FormatsCount()
        {
            var vm = Vm(processing: 1, completed: 8);
            Assert.True(vm.ShowCompleted);
            Assert.Equal("8 done", vm.CompletedText);
        }

        [Fact]
        public void ZeroCompleted_Hidden()
        {
            var vm = Vm(processing: 1, completed: 0);
            Assert.False(vm.ShowCompleted);
        }
    }
}
