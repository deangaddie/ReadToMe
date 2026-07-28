using Read2Me.Services.Queueing;
using Xunit;

namespace Read2Me.Tests.Services.Queueing
{
    public class AttemptStateTests
    {
        [Fact]
        public void Default_SpendsNothing()
        {
            var a = default(AttemptState);
            Assert.Equal(0, a.Retries);
            Assert.Equal(0, a.Busies);
        }

        [Fact]
        public void WithRetry_BumpsRetriesOnly()
        {
            var a = default(AttemptState).WithRetry();
            Assert.Equal(1, a.Retries);
            Assert.Equal(0, a.Busies);
        }

        [Fact]
        public void WithBusy_BumpsBusiesOnly()
        {
            var a = default(AttemptState).WithBusy();
            Assert.Equal(0, a.Retries);
            Assert.Equal(1, a.Busies);
        }

        [Fact]
        public void Budgets_AreIndependentAndAccumulate()
        {
            var a = default(AttemptState).WithBusy().WithBusy().WithRetry().WithBusy();
            Assert.Equal(1, a.Retries);
            Assert.Equal(3, a.Busies);
        }

        [Fact]
        public void With_ReturnsANewValue_LeavingTheOriginalUnspent()
        {
            var original = default(AttemptState);
            _ = original.WithRetry();
            Assert.Equal(0, original.Retries);
        }
    }
}
