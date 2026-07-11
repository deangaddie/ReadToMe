using Read2Me.App.State;
using Read2Me.Core.Models;
using Read2Me.Services.Characters;
using Read2Me.Services.Events;
using Read2Me.Services.Llm;
using Xunit;

namespace Read2Me.Tests.State
{
    public class AttributionProgressStateTests
    {
        private static QueuedParagraph Para() =>
            new(new ProjectFolderId("f"), Guid.NewGuid(), "p", Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        private static (AttributionProgressState State, EventBroadcaster<LlmStreamEvent> Stream, CharacterQueueService Queue) Make()
        {
            var stream = new EventBroadcaster<LlmStreamEvent>();
            var queue = new CharacterQueueService();
            return (new AttributionProgressState(stream, queue), stream, queue);
        }

        [Fact]
        public void NoEscalation_ByDefault()
        {
            var (state, _, _) = Make();
            Assert.False(state.HasEscalation);
            Assert.Null(state.Step);
        }

        [Fact]
        public void EscalationStarted_LatchesLatestStep()
        {
            var (state, stream, queue) = Make();
            queue.Enqueue(new[] { Para() }); // queue non-idle so the latch is not cleared

            stream.Publish(new EscalationStarted(1, "B", 3));

            Assert.True(state.HasEscalation);
            Assert.Equal(1, state.Step);
            Assert.Equal("B", state.ConfigName);
            Assert.Equal(3, state.ItemCount);

            stream.Publish(new EscalationStarted(2, "C", 1));
            Assert.Equal(2, state.Step);
            Assert.Equal("C", state.ConfigName);
            Assert.Equal(1, state.ItemCount);
        }

        [Fact]
        public void NonEscalationEvents_Ignored()
        {
            var (state, stream, _) = Make();
            stream.Publish(new RequestStarted("preview", "prompt"));
            stream.Publish(new ContentDelta("x"));
            Assert.False(state.HasEscalation);
        }

        [Fact]
        public void QueueDrainedToIdle_ClearsLatch()
        {
            var (state, stream, queue) = Make();
            queue.Enqueue(new[] { Para() });
            stream.Publish(new EscalationStarted(1, "B", 2));
            Assert.True(state.HasEscalation);

            queue.CancelAll(); // queue back to idle → latch clears on Changed

            Assert.False(state.HasEscalation);
            Assert.Null(state.Step);
        }

        [Fact]
        public void ChangedFires_OnEscalationAndOnClear()
        {
            var (state, stream, queue) = Make();
            queue.Enqueue(new[] { Para() });
            var fired = 0;
            state.Changed += () => fired++;

            stream.Publish(new EscalationStarted(1, "B", 2));
            Assert.True(fired >= 1);

            var afterEscalation = fired;
            queue.CancelAll();
            Assert.True(fired > afterEscalation);
        }
    }
}
