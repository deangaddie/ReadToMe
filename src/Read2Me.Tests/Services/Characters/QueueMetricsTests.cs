using Read2Me.Services.Characters;
using Xunit;

namespace Read2Me.Tests.Services.Characters
{
    public class QueueMetricsTests
    {
        [Fact]
        public void NoCompletions_AverageIsZero()
        {
            var m = new QueueMetrics();
            Assert.Equal(0.0, m.AverageSecondsPerParagraph);
            Assert.Equal(0, m.CompletedCount);
        }

        [Fact]
        public void OneCompletion_AverageEqualsElapsed()
        {
            var m = new QueueMetrics();
            m.RecordCompletion(5.0);
            Assert.Equal(5.0, m.AverageSecondsPerParagraph);
            Assert.Equal(1, m.CompletedCount);
        }

        [Fact]
        public void AverageAcrossThreeCompletions_IsIncrementalMean()
        {
            var m = new QueueMetrics();
            m.RecordCompletion(2);
            m.RecordCompletion(4);
            m.RecordCompletion(6);
            Assert.Equal(4.0, m.AverageSecondsPerParagraph, precision: 6);
            Assert.Equal(3, m.CompletedCount);
        }
    }
}
