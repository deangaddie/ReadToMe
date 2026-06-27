using Read2Me.Services.Events;
using Xunit;

namespace Read2Me.Tests.Services.Events
{
    public class EventBroadcasterTests
    {
        [Fact]
        public void Publish_InvokesSubscribedHandler_WithSameInstance()
        {
            var broadcaster = new EventBroadcaster<string>();
            string? received = null;
            broadcaster.Event += e => received = e;

            broadcaster.Publish("hello");

            Assert.Equal("hello", received);
        }

        [Fact]
        public void Publish_NoSubscribers_DoesNotThrow()
        {
            var broadcaster = new EventBroadcaster<int>();
            broadcaster.Publish(42); // should not throw
        }

        [Fact]
        public void Publish_MultipleSubscribers_AllInvoked()
        {
            var broadcaster = new EventBroadcaster<int>();
            int count = 0;
            broadcaster.Event += _ => count++;
            broadcaster.Event += _ => count++;

            broadcaster.Publish(1);

            Assert.Equal(2, count);
        }
    }
}
