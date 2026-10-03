using System.Runtime.CompilerServices;
using Read2Me.AppData.Entities;
using Read2Me.Core.Models;
using Read2Me.Services.Characters;
using Xunit;

namespace Read2Me.Tests.Services.Characters
{
    /// <summary>
    /// The style router is the "existing path unchanged" guarantee (spec §4.1): <c>Full</c> and
    /// <c>Simple</c> reach the existing step with the very options the walk built, and only
    /// <c>Chapter</c> reaches the chapter pass.
    /// </summary>
    public class StyleRoutedChainStepTests
    {
        private static readonly QueuedParagraph Item =
            new(new ProjectFolderId("book"), Guid.NewGuid(), "P", Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        private sealed class RecordingStep : IChainStep
        {
            public List<ChainStepOptions> Seen { get; } = [];

            public async IAsyncEnumerable<(QueuedParagraph Item, StepOutcome Step)> RunAsync(
                IReadOnlyList<QueuedParagraph> items, ChainStepOptions opts, AttributionQueueCallbacks? callbacks,
                [EnumeratorCancellation] CancellationToken ct)
            {
                Seen.Add(opts);
                await Task.Yield();
                foreach (var i in items)
                    yield return (i, new StepOutcome(
                        new AttributionOutcome(AttributionStatus.Resolved, null, null), EscalationTrigger.None));
            }
        }

        private static async Task<int> DrainAsync(IChainStep step, ChainStepOptions opts)
        {
            var n = 0;
            await foreach (var _ in step.RunAsync([Item], opts, null, CancellationToken.None))
                n++;
            return n;
        }

        [Theory]
        [InlineData(AttributionPromptStyle.Full)]
        [InlineData(AttributionPromptStyle.Simple)]
        public async Task Full_and_simple_reach_the_existing_step_with_options_unchanged(AttributionPromptStyle style)
        {
            var full = new RecordingStep();
            var chapter = new RecordingStep();
            var opts = new ChainStepOptions(
                new LlmServerConfig { Name = "c" }, IsFinal: false, SelfConsistency: true, Thinking: true,
                Style: style, TemperatureOverride: 0.7);

            var yielded = await DrainAsync(new StyleRoutedChainStep(full, chapter), opts);

            Assert.Equal(1, yielded);
            Assert.Same(opts, Assert.Single(full.Seen));
            Assert.Empty(chapter.Seen);
        }

        [Fact]
        public async Task Config_style_is_used_when_the_rung_sets_none()
        {
            var full = new RecordingStep();
            var chapter = new RecordingStep();
            var opts = new ChainStepOptions(
                new LlmServerConfig { Name = "c", PromptStyle = AttributionPromptStyle.Chapter },
                IsFinal: true, SelfConsistency: false);

            await DrainAsync(new StyleRoutedChainStep(full, chapter), opts);

            Assert.Same(opts, Assert.Single(chapter.Seen));
            Assert.Empty(full.Seen);
        }

        [Fact]
        public async Task Chapter_reaches_the_chapter_pass()
        {
            var full = new RecordingStep();
            var chapter = new RecordingStep();
            var opts = new ChainStepOptions(
                new LlmServerConfig { Name = "c" }, IsFinal: true, SelfConsistency: false,
                Style: AttributionPromptStyle.Chapter);

            var yielded = await DrainAsync(new StyleRoutedChainStep(full, chapter), opts);

            Assert.Equal(1, yielded);
            Assert.Same(opts, Assert.Single(chapter.Seen));
            Assert.Empty(full.Seen);
        }
    }
}
