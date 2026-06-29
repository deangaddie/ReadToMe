using Read2Me.App.State;
using Read2Me.Services.Audio.Assembly;
using Xunit;

namespace Read2Me.Tests.State
{
    public class AssemblyButtonViewModelTests
    {
        private static AssemblyButtonViewModel Vm(
            int audioRemaining = 0,
            bool audioQueueBusy = false,
            bool isRunning = false,
            AssemblyPhase? phase = null,
            double encodePercent = 0) =>
            AssemblyButtonViewModel.For(audioRemaining, audioQueueBusy, isRunning, phase, encodePercent);

        // ── Button enabled/disabled ─────────────────────────────────────────

        [Fact]
        public void Idle_NoPreconditions_IsEnabled()
        {
            var vm = Vm();
            Assert.True(vm.IsEnabled);
            Assert.Null(vm.DisabledReason);
        }

        [Fact]
        public void AssemblyRunning_IsDisabled_NoTooltip()
        {
            var vm = Vm(isRunning: true, phase: AssemblyPhase.Encode);
            Assert.False(vm.IsEnabled);
            Assert.Null(vm.DisabledReason);
        }

        [Theory]
        [InlineData(1)]
        [InlineData(5)]
        public void AudioRemaining_IdleNotRunning_IsEnabled(int remaining)
        {
            var vm = Vm(audioRemaining: remaining);
            Assert.True(vm.IsEnabled);
            Assert.Null(vm.DisabledReason);
        }

        [Fact]
        public void AudioQueueBusy_IsDisabled()
        {
            var vm = Vm(audioQueueBusy: true);
            Assert.False(vm.IsEnabled);
            Assert.Equal("Audio queue is busy", vm.DisabledReason);
        }

        [Fact]
        public void AudioRemaining_AndQueueBusy_IsDisabled()
        {
            var vm = Vm(audioRemaining: 3, audioQueueBusy: true);
            Assert.False(vm.IsEnabled);
            Assert.Equal("Audio queue is busy", vm.DisabledReason);
        }

        // ── Phase labels ────────────────────────────────────────────────────

        [Theory]
        [InlineData(AssemblyPhase.Gather, "Gathering")]
        [InlineData(AssemblyPhase.Silence, "Generating silence")]
        [InlineData(AssemblyPhase.ProbeConcat, "Building")]
        [InlineData(AssemblyPhase.Encode, "Encoding")]
        [InlineData(AssemblyPhase.Finalize, "Finalizing")]
        public void Phase_MapsToLabel(AssemblyPhase phase, string expected)
        {
            var vm = Vm(isRunning: true, phase: phase);
            Assert.Equal(expected, vm.PhaseLabel);
        }

        [Fact]
        public void NullPhase_EmptyLabel()
        {
            var vm = Vm();
            Assert.Equal(string.Empty, vm.PhaseLabel);
        }

        // ── Encode percent ──────────────────────────────────────────────────

        [Theory]
        [InlineData(0.0, "0%")]
        [InlineData(0.5, "50%")]
        [InlineData(1.0, "100%")]
        public void Encode_Phase_ShowsPercent(double fraction, string expected)
        {
            var vm = Vm(isRunning: true, phase: AssemblyPhase.Encode, encodePercent: fraction);
            Assert.Equal(expected, vm.EncodePercentText);
        }

        [Fact]
        public void NonEncode_Phase_NoPercentText()
        {
            var vm = Vm(isRunning: true, phase: AssemblyPhase.Gather, encodePercent: 0.5);
            Assert.Equal(string.Empty, vm.EncodePercentText);
        }
    }
}
