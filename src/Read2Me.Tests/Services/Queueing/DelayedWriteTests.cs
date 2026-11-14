using System.Threading.Channels;
using Read2Me.Services.Queueing;
using Xunit;

namespace Read2Me.Tests.Services.Queueing
{
    /// <summary>The retry writes' mechanics: the wait for a Restore to end, and a Cancel all dropping the wait.</summary>
    public class DelayedWriteTests
    {
        [Fact]
        public async Task ScheduleWhen_WritesOnlyOnceTheTaskCompletes()
        {
            var channel = Channel.CreateUnbounded<int>();
            var restoreEnded = new TaskCompletionSource();

            var write = DelayedWrite.ScheduleWhen(channel.Writer, 7, restoreEnded.Task, CancellationToken.None);
            await Task.Delay(50, TestContext.Current.CancellationToken);
            Assert.False(channel.Reader.TryPeek(out _));

            restoreEnded.SetResult();
            await write.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            Assert.True(channel.Reader.TryRead(out var written));
            Assert.Equal(7, written);
        }

        [Fact]
        public async Task ScheduleWhen_CancelledWhileWaiting_WritesNothing()
        {
            var channel = Channel.CreateUnbounded<int>();
            var restoreEnded = new TaskCompletionSource();
            using var cts = new CancellationTokenSource();

            var write = DelayedWrite.ScheduleWhen(channel.Writer, 7, restoreEnded.Task, cts.Token);
            cts.Cancel();
            await write.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            restoreEnded.SetResult();

            Assert.False(channel.Reader.TryPeek(out _));
        }
    }
}
