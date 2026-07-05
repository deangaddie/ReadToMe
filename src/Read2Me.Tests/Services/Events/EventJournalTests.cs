using Read2Me.Services.Events;
using Xunit;

namespace Read2Me.Tests.Services.Events
{
    public class EventJournalTests
    {
        private abstract record TestEvent;
        private sealed record TurnStart(int Turn) : TestEvent;
        private sealed record Delta(string Text) : TestEvent;

        private static (EventBroadcaster<TestEvent> Broadcaster, EventJournal<TestEvent> Journal) Make()
        {
            var broadcaster = new EventBroadcaster<TestEvent>();
            var journal = new EventJournal<TestEvent>(broadcaster, e => e is TurnStart);
            return (broadcaster, journal);
        }

        [Fact]
        public void LateSubscriber_ReplaysCurrentTurn()
        {
            var (broadcaster, journal) = Make();
            broadcaster.Publish(new TurnStart(1));
            broadcaster.Publish(new Delta("a"));
            broadcaster.Publish(new Delta("b"));

            var received = new List<TestEvent>();
            journal.Subscribe(received.Add);

            Assert.Equal([new TurnStart(1), new Delta("a"), new Delta("b")], received);
        }

        [Fact]
        public void NewTurn_DropsPreviousTurnFromReplay()
        {
            var (broadcaster, journal) = Make();
            broadcaster.Publish(new TurnStart(1));
            broadcaster.Publish(new Delta("old"));
            broadcaster.Publish(new TurnStart(2));
            broadcaster.Publish(new Delta("new"));

            var received = new List<TestEvent>();
            journal.Subscribe(received.Add);

            Assert.Equal([new TurnStart(2), new Delta("new")], received);
        }

        [Fact]
        public void FinishedTurn_StaysAvailable_UntilNextTurnStarts()
        {
            var (broadcaster, journal) = Make();
            broadcaster.Publish(new TurnStart(1));
            broadcaster.Publish(new Delta("done"));
            // No new turn yet — a subscriber between items still sees the last one.

            var received = new List<TestEvent>();
            journal.Subscribe(received.Add);

            Assert.Equal([new TurnStart(1), new Delta("done")], received);
        }

        [Fact]
        public void LiveEventsAfterSubscribe_DeliveredOnce()
        {
            var (broadcaster, journal) = Make();
            broadcaster.Publish(new TurnStart(1));

            var received = new List<TestEvent>();
            journal.Subscribe(received.Add);
            broadcaster.Publish(new Delta("live"));

            Assert.Equal([new TurnStart(1), new Delta("live")], received);
        }

        [Fact]
        public void NoEventsYet_SubscribeReplaysNothing()
        {
            var (_, journal) = Make();
            var received = new List<TestEvent>();
            journal.Subscribe(received.Add);
            Assert.Empty(received);
        }

        [Fact]
        public void Unsubscribe_StopsDelivery()
        {
            var (broadcaster, journal) = Make();
            var received = new List<TestEvent>();
            journal.Subscribe(received.Add);

            journal.Unsubscribe(received.Add);
            broadcaster.Publish(new TurnStart(1));

            Assert.Empty(received);
        }
    }
}
