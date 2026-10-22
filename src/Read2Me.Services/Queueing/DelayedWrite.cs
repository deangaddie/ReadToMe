using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Read2Me.Services.Queueing
{
    /// <summary>
    /// The <see cref="Disposition.RetryAfter"/> write, shared by both queues. Mechanics, not policy —
    /// the delay itself is already decided by <see cref="QueueDisposition.Backoff"/>.
    /// </summary>
    public static class DelayedWrite
    {
        /// <summary>
        /// Waits out <paramref name="delay"/> and writes <paramref name="item"/> to
        /// <paramref name="writer"/>.
        /// <para>
        /// It takes the <b>writer</b>, not the queue, and that is what makes the write safe: a queue
        /// that swaps its channel while the delay runs (cancel-all) completes this writer, so the late
        /// write fails harmlessly instead of resurrecting cancelled work on the replacement channel.
        /// Re-reading a <c>_channel</c> field after the delay would do the opposite.
        /// <paramref name="ct"/> is not load-bearing for that correctness; it only ends a pending
        /// delay promptly rather than letting it linger.
        /// </para>
        /// <para>
        /// A non-positive delay writes synchronously, so a caller that wants "back on the queue now"
        /// does not have to special-case it.
        /// </para>
        /// </summary>
        public static Task Schedule<TItem>(
            ChannelWriter<TItem> writer, TItem item, TimeSpan delay, CancellationToken ct)
        {
            if (delay <= TimeSpan.Zero)
            {
                writer.TryWrite(item);
                return Task.CompletedTask;
            }
            return WriteWhenAsync(writer, item, Task.Delay(delay, ct), ct);
        }

        /// <summary>
        /// The <see cref="Disposition.RetryAfterRestore"/> write: as <see cref="Schedule{TItem}"/>,
        /// but waiting for <paramref name="until"/> — the project's Restore ending — rather than a delay.
        /// </summary>
        public static Task ScheduleWhen<TItem>(
            ChannelWriter<TItem> writer, TItem item, Task until, CancellationToken ct)
        {
            if (until.IsCompleted)
            {
                writer.TryWrite(item);
                return Task.CompletedTask;
            }
            return WriteWhenAsync(writer, item, until, ct);
        }

        private static async Task WriteWhenAsync<TItem>(
            ChannelWriter<TItem> writer, TItem item, Task until, CancellationToken ct)
        {
            try
            {
                await until.WaitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                // The queue was cleared (CancelAll) while this item waited — drop it.
                return;
            }
            writer.TryWrite(item);
        }
    }
}
