using Read2Me.Services.Queueing;
using Xunit;

namespace Read2Me.Tests.Services.Queueing
{
    public class QueueStateStoreTests
    {
        private sealed record Outcome(string Reason);

        private static QueueStateStore<int, Outcome> NewStore() => new();

        [Fact]
        public void TryMarkQueued_FirstTime_ReturnsTrueAndSetsQueued()
        {
            var store = NewStore();

            Assert.True(store.TryMarkQueued(1));
            Assert.Equal(QueueItemStatus.Queued, store.StatusOf(1));
        }

        [Fact]
        public void TryMarkQueued_Duplicate_ReturnsFalse()
        {
            var store = NewStore();
            store.TryMarkQueued(1);

            Assert.False(store.TryMarkQueued(1));
        }

        [Fact]
        public void MarkProcessing_SetsProcessing()
        {
            var store = NewStore();
            store.TryMarkQueued(1);

            store.MarkProcessing(1);

            Assert.Equal(QueueItemStatus.Processing, store.StatusOf(1));
        }

        [Fact]
        public void ReturnToQueued_SetsQueued_AndClearsPriorOutcome()
        {
            var store = NewStore();
            store.TryMarkQueued(1);
            store.MarkProcessing(1);
            store.Abandon(1, new Outcome("failed"));

            store.ReturnToQueued(1);

            Assert.Equal(QueueItemStatus.Queued, store.StatusOf(1));
            Assert.Null(store.OutcomeOf(1));
        }

        [Fact]
        public void Settle_WithoutOutcome_RemovesStatus_AndRecordsCompletion()
        {
            var store = NewStore();
            store.TryMarkQueued(1);
            store.MarkProcessing(1);

            store.Settle(1, elapsedSeconds: 5.0);

            Assert.Null(store.StatusOf(1));
            var (completed, avg) = store.Metrics();
            Assert.Equal(1, completed);
            Assert.Equal(5.0, avg);
        }

        [Fact]
        public void Settle_WithoutOutcome_ClearsAnyStaleOutcome()
        {
            var store = NewStore();
            store.TryMarkQueued(1);
            store.MarkProcessing(1);
            store.Abandon(1, new Outcome("unfinished"));

            store.Settle(1);

            Assert.Null(store.OutcomeOf(1));
        }

        [Fact]
        public void Settle_WithOutcome_RecordsOutcome_AndCountsTowardAverage()
        {
            var store = NewStore();
            store.TryMarkQueued(1);
            store.MarkProcessing(1);

            store.Settle(1, new Outcome("unfinished"), 5.0);

            Assert.Null(store.StatusOf(1));
            Assert.Equal("unfinished", store.OutcomeOf(1)!.Reason);
            var (completed, avg) = store.Metrics();
            Assert.Equal(1, completed);
            Assert.Equal(5.0, avg);
        }

        [Fact]
        public void Settle_NullElapsed_MeasuresFromMarkProcessing()
        {
            var store = NewStore();
            store.TryMarkQueued(1);
            store.MarkProcessing(1);

            store.Settle(1);

            Assert.Equal(1, store.Metrics().completed);
            Assert.Equal(0, store.CurrentElapsedSeconds());
        }

        [Fact]
        public void Abandon_RecordsOutcome_RemovesStatus_NoCompletion()
        {
            var store = NewStore();
            store.TryMarkQueued(1);
            store.MarkProcessing(1);

            store.Abandon(1, new Outcome("failed"));

            Assert.Null(store.StatusOf(1));
            Assert.Equal("failed", store.OutcomeOf(1)!.Reason);
            Assert.Equal(0, store.Metrics().completed);
        }

        [Fact]
        public void TryMarkQueued_ClearsPriorOutcome()
        {
            var store = NewStore();
            store.Abandon(1, new Outcome("failed"));

            store.TryMarkQueued(1);

            Assert.Null(store.OutcomeOf(1));
        }

        [Fact]
        public void ClearOutcome_RemovesOutcome_ReturnsWhetherRemoved()
        {
            var store = NewStore();
            store.Abandon(1, new Outcome("failed"));

            Assert.True(store.ClearOutcome(1));
            Assert.False(store.ClearOutcome(1));
            Assert.Null(store.OutcomeOf(1));
        }

        [Fact]
        public void CountStatuses_CountsQueuedAndProcessing()
        {
            var store = NewStore();
            store.TryMarkQueued(1);
            store.TryMarkQueued(2);
            store.TryMarkQueued(3);
            store.MarkProcessing(3);

            var (queued, processing) = store.CountStatuses();

            Assert.Equal(2, queued);
            Assert.Equal(1, processing);
        }

        [Fact]
        public void Metrics_RollingAverageAcrossCompletions()
        {
            var store = NewStore();
            store.Settle(1, elapsedSeconds: 2);
            store.Settle(2, elapsedSeconds: 4);
            store.Settle(3, elapsedSeconds: 6);

            var (completed, avg) = store.Metrics();

            Assert.Equal(3, completed);
            Assert.Equal(4.0, avg, precision: 6);
        }

        [Fact]
        public void ClearAll_RemovesStatus_PreservesOutcomes_KeepsMetrics()
        {
            var store = NewStore();
            store.Settle(1, elapsedSeconds: 3);
            store.TryMarkQueued(2);
            store.Abandon(3, new Outcome("failed"));

            store.ClearAll();

            Assert.Null(store.StatusOf(2));
            Assert.NotNull(store.OutcomeOf(3));   // outcomes survive cancel
            Assert.Equal((0, 0), store.CountStatuses());
            Assert.Equal(1, store.Metrics().completed);
        }

        [Fact]
        public void CurrentElapsedSeconds_ZeroWhenIdle()
        {
            var store = NewStore();
            Assert.Equal(0, store.CurrentElapsedSeconds());
        }
    }
}
