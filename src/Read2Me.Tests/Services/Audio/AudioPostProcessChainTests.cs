using Microsoft.Extensions.Logging.Abstractions;
using Read2Me.Services.Audio;
using Xunit;

namespace Read2Me.Tests.Services.Audio
{
    public class AudioPostProcessChainTests
    {
        /// <summary>Appends its own marker byte, so the carry-forward is visible in the output bytes.</summary>
        private sealed class MarkerStep(string stepId, byte marker, bool applied = true, string? reason = null)
            : IAudioPostProcessStep
        {
            public string StepId => stepId;
            public List<byte[]> Inputs { get; } = [];

            public Task<PostProcessResult> ProcessAsync(
                byte[] wav, string? ffmpegPath, string? settingsJson, CancellationToken ct)
            {
                Inputs.Add(wav);
                // A skipped step returns its input unchanged — already the PostProcessResult contract.
                var audio = applied ? wav.Append(marker).ToArray() : wav;
                return Task.FromResult(new PostProcessResult(audio, applied, reason));
            }
        }

        private static AudioPostProcessChain NewChain(params IAudioPostProcessStep[] steps) =>
            new(steps, NullLogger<AudioPostProcessChain>.Instance);

        private static AudioPostProcessStepConfig Config(string stepId) =>
            AudioPostProcessStepConfig.Create(stepId, enabled: true, new DenoiseSettings());

        [Fact]
        public async Task Runs_the_steps_in_the_order_the_chain_lists_them_carrying_bytes_forward()
        {
            var a = new MarkerStep("a", 1);
            var b = new MarkerStep("b", 2);

            var result = await NewChain(b, a).RunAsync(
                [0], [Config("a"), Config("b")], null, CancellationToken.None);

            // Registration order is irrelevant — the chain's order wins.
            Assert.Equal([0, 1, 2], result.Audio);
            Assert.Equal([0], a.Inputs[0]);
            Assert.Equal([0, 1], b.Inputs[0]);
        }

        [Fact]
        public async Task Hands_back_every_step_s_own_output()
        {
            // This is what makes the editor's per-step cumulative players free.
            var result = await NewChain(new MarkerStep("a", 1), new MarkerStep("b", 2))
                .RunAsync([0], [Config("a"), Config("b")], null, CancellationToken.None);

            Assert.Equal([0, 1], result.Steps[0].Audio);
            Assert.Equal([0, 1, 2], result.Steps[1].Audio);
            Assert.Equal(result.Audio, result.Steps[^1].Audio);
        }

        [Fact]
        public async Task Skipped_step_returns_its_input_and_the_chain_continues()
        {
            var result = await NewChain(
                    new MarkerStep("a", 1),
                    new MarkerStep("b", 2, applied: false, reason: "ffmpeg not found"),
                    new MarkerStep("c", 3))
                .RunAsync([0], [Config("a"), Config("b"), Config("c")], null, CancellationToken.None);

            Assert.Equal([0, 1, 3], result.Audio);

            var skipped = result.Steps[1];
            Assert.False(skipped.Applied);
            Assert.Equal("ffmpeg not found", skipped.Reason);
            // Its player is identical to the one above it — the user hears the step do nothing.
            Assert.Equal(result.Steps[0].Audio, skipped.Audio);
        }

        [Fact]
        public async Task Unregistered_step_id_is_skipped_not_thrown()
        {
            var result = await NewChain(new MarkerStep("a", 1))
                .RunAsync([0], [Config("a"), Config("nope")], null, CancellationToken.None);

            Assert.Equal([0, 1], result.Audio);
            Assert.Equal(["a"], result.Steps.Select(s => s.StepId));
        }

        [Fact]
        public async Task Empty_chain_returns_the_input()
        {
            var result = await NewChain().RunAsync([9, 9], [], null, CancellationToken.None);

            Assert.Equal([9, 9], result.Audio);
            Assert.Empty(result.Steps);
        }

        [Fact]
        public async Task Cancellation_propagates()
        {
            using var cts = new CancellationTokenSource();
            await cts.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                NewChain(new MarkerStep("a", 1)).RunAsync([0], [Config("a")], null, cts.Token));
        }
    }
}
