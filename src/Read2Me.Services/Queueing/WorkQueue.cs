using System.Threading.Channels;
using Read2Me.Core.Models;
using Read2Me.Services.History;

namespace Read2Me.Services.Queueing
{
    /// <summary>One queued item's identity: its project and the id of the paragraph or item it works on.</summary>
    public readonly record struct WorkItemKey(ProjectFolderId Folder, Guid Id);

    /// <summary>
    /// A <see cref="WorkQueue{TItem}"/> payload. It names its own key and carries its own
    /// <see cref="AttemptState"/>, so the queue can deduplicate, gate and re-queue it without an adapter.
    /// </summary>
    public interface IQueuedWork<TSelf> where TSelf : IQueuedWork<TSelf>
    {
        WorkItemKey Key { get; }

        AttemptState Attempts { get; }

        /// <summary>The same payload with <paramref name="attempts"/> in place of its own.</summary>
        TSelf WithAttempts(AttemptState attempts);
    }

    /// <summary>The marker a settled-but-not-completed item keeps: <b>Queue outcome</b> (CONTEXT: queueing).</summary>
    public enum QueueOutcomeKind { Failed, Unfinished }

    /// <summary>
    /// A <b>Queue outcome</b>: why an item the queue settled is not simply done. A completed item has none.
    /// </summary>
    public sealed record QueueOutcome(QueueOutcomeKind Kind, string? Reason);

    /// <summary>How a Work queue names its busy work to the Project activity gate: "Audio: 3 items left".</summary>
    public sealed record WorkQueueBusy(BusySourceKind Kind, string Label, string Noun);

    /// <summary>A Work queue's counts and timings, which each queue maps to its own wire snapshot.</summary>
    public readonly record struct WorkQueueCounts(
        int Queued,
        int Processing,
        double AverageSeconds,
        double EstimatedSecondsRemaining,
        int Completed,
        double CurrentItemElapsedSeconds);

    /// <summary>
    /// A <b>Work queue</b> (CONTEXT: queueing) — what the Character Queue and the Audio Queue each hold
    /// and plug into. It owns the channel and the state, the start through the Project activity gate,
    /// the item cancellation token, the one <see cref="Apply"/> that runs every <see cref="Disposition"/>,
    /// Cancel all, and the busy answer. The queue that holds it adds only what is its own: the work
    /// (its processor), and what it remembers about a completed item (<c>onComplete</c>).
    /// </summary>
    public sealed class WorkQueue<TItem> : IQueueSource<TItem>, IBusySource where TItem : IQueuedWork<TItem>
    {
        private Channel<TItem> _channel = NewChannel();
        private CancellationTokenSource _itemCts = new();
        private readonly QueueStateStore<WorkItemKey, QueueOutcome> _store = new();
        private readonly ProjectActivity _activity;
        private readonly WorkQueueBusy _busy;
        private readonly Action<WorkItemKey>? _onComplete;
        private readonly Action? _onCancel;

        /// <param name="onComplete">
        /// Runs inside the <see cref="Disposition.Complete"/> arm, before <see cref="Changed"/>, so an
        /// item cannot complete without it.
        /// </param>
        /// <param name="onCancel">Runs inside <see cref="CancelAll"/>, after the state is cleared and before <see cref="Changed"/>.</param>
        public WorkQueue(ProjectActivity activity, WorkQueueBusy busy, Action<WorkItemKey>? onComplete = null, Action? onCancel = null)
        {
            _activity = activity;
            _busy = busy;
            _onComplete = onComplete;
            _onCancel = onCancel;
            activity.Register(this);
        }

        public event Action? Changed;

        /// <summary>
        /// Raised by <see cref="CancelAll"/> before it clears anything, so a listener can tell a
        /// cancelled folder from one that drained — the <see cref="Changed"/> that follows looks the same.
        /// </summary>
        public event Action? Cancelling;

        public ChannelReader<TItem> Reader => _channel.Reader;

        /// <summary>Cancelled by <see cref="CancelAll"/>, so the item in flight stops with the queue.</summary>
        public CancellationToken ItemCancellationToken => _itemCts.Token;

        /// <summary>
        /// Queues the items, or refuses them all — queuing and announcing nothing — when the gate
        /// refuses a start for any of their projects. An item already queued or processing is not
        /// queued twice. Null means queued. The queuing is the gate's start, so a Restore either sees
        /// the items as busy or is seen by the enqueue.
        /// </summary>
        public ActivityRefusal? Enqueue(IEnumerable<TItem> items)
        {
            var list = items.ToList();
            var refusal = _activity.TryStart(list.Select(i => i.Key.Folder), () =>
            {
                foreach (var item in list)
                {
                    if (_store.TryMarkQueued(item.Key))
                        _channel.Writer.TryWrite(item);
                }
            });
            if (refusal is null)
                Changed?.Invoke();
            return refusal;
        }

        /// <summary>The item has gone in flight.</summary>
        public void MarkProcessing(TItem item)
        {
            _store.MarkProcessing(item.Key);
            Changed?.Invoke();
        }

        /// <summary>
        /// Returns an in-flight item to Queued <i>without</i> putting it back on the channel: its
        /// processor still holds it and will run it again itself. Spends no retry budget. An item
        /// <see cref="CancelAll"/> cleared stays cancelled.
        /// </summary>
        public void Defer(TItem item)
        {
            _store.ReturnToQueued(item.Key);
            Changed?.Invoke();
        }

        /// <summary>
        /// Returns <paramref name="first"/> plus every remaining queued item, drained from the head of
        /// the queue in the order it was enqueued. Marks, settles and requeues nothing. Only the
        /// channel's single reader (the queue worker) may call it.
        /// </summary>
        public IReadOnlyList<TItem> DrainAll(TItem first)
        {
            var all = new List<TItem> { first };
            while (_channel.Reader.TryRead(out var item))
                all.Add(item);
            return all;
        }

        /// <summary>
        /// Runs the transition the decision produced. Total: every <see cref="Disposition"/> member
        /// executes and no arm throws. It performs no work — that stays with the processor.
        /// <para>
        /// Each retry arm bumps exactly the counter its own <see cref="QueueDisposition.Decide"/> arm
        /// reads: <see cref="Disposition.RetryOnce"/> spends the once-only <see cref="AttemptState.Retries"/>
        /// budget a watchdog recovery is allowed, and <see cref="Disposition.RetryAfter"/> spends an
        /// <see cref="AttemptState.Busies"/> so the next backoff grows without touching the once-only
        /// budget. An item <see cref="CancelAll"/> cleared while in flight stays cancelled: no retry arm
        /// returns it to Queued or puts it back on the channel.
        /// </para>
        /// </summary>
        public void Apply(TItem item, Disposition disposition)
        {
            var key = item.Key;
            switch (disposition)
            {
                case Disposition.Complete complete:
                    _store.Settle(key, elapsedSeconds: complete.Elapsed);
                    _onComplete?.Invoke(key);
                    break;

                case Disposition.Unfinished unfinished:
                    _store.Settle(key, new QueueOutcome(QueueOutcomeKind.Unfinished, unfinished.Reason), unfinished.Elapsed);
                    break;

                case Disposition.Failed failed:
                    _store.Abandon(key, new QueueOutcome(QueueOutcomeKind.Failed, failed.Reason));
                    break;

                // Back on the channel now, where it waits on the Processing gate recovery closed.
                case Disposition.RetryOnce:
                    if (_store.ReturnToQueued(key))
                        _channel.Writer.TryWrite(item.WithAttempts(item.Attempts.WithRetry()));
                    break;

                // Queued now; the write waits out the delay against the writer captured now, so a
                // CancelAll during the backoff drops the item instead of resurrecting it on the
                // replacement channel.
                case Disposition.RetryAfter retryAfter:
                    if (!_store.ReturnToQueued(key)) break;
                    _ = DelayedWrite.Schedule(
                        _channel.Writer, item.WithAttempts(item.Attempts.WithBusy()), retryAfter.Delay, _itemCts.Token);
                    break;

                // As RetryAfter, but the wait is the project's Restore, and no budget is spent.
                case Disposition.RetryAfterRestore:
                    if (!_store.ReturnToQueued(key)) break;
                    _ = DelayedWrite.ScheduleWhen(
                        _channel.Writer, item, _activity.WhenRestoreEnded(key.Folder), _itemCts.Token);
                    break;
            }

            Changed?.Invoke();
        }

        /// <summary>
        /// <b>Cancel all</b>: stops the item in flight (its token), drops every waiting item and every
        /// pending retry (the channel they would write to is completed), and clears queued/processing
        /// state. Queue outcomes survive.
        /// </summary>
        public void CancelAll()
        {
            Cancelling?.Invoke();

            var old = Interlocked.Exchange(ref _itemCts, new CancellationTokenSource());
            old.Cancel();
            old.Dispose();

            Interlocked.Exchange(ref _channel, NewChannel()).Writer.TryComplete();

            _store.ClearAll();
            _onCancel?.Invoke();

            Changed?.Invoke();
        }

        public QueueItemStatus? StatusOf(WorkItemKey key) => _store.StatusOf(key);

        public QueueOutcome? OutcomeOf(WorkItemKey key) => _store.OutcomeOf(key);

        /// <summary>Clears the item's Queue outcome; raises <see cref="Changed"/> only when there was one.</summary>
        public void ClearOutcome(WorkItemKey key)
        {
            if (_store.ClearOutcome(key))
                Changed?.Invoke();
        }

        /// <summary>Every item queued or processing (a moment-in-time copy).</summary>
        public IReadOnlyCollection<WorkItemKey> TrackedKeys() => _store.TrackedKeys();

        /// <summary>Every item queued, processing, or carrying a Queue outcome (a moment-in-time copy).</summary>
        public IEnumerable<WorkItemKey> KnownKeys() => _store.TrackedKeys().Concat(_store.OutcomeKeys());

        /// <summary>
        /// The folder's items still queued or processing, or null when it has none. An item stays
        /// tracked from enqueue until its disposition, so the one in flight always counts; settled
        /// items are finished work and do not.
        /// </summary>
        public BusySource? BusyFor(ProjectFolderId folder)
        {
            var left = _store.TrackedKeys().Count(k => k.Folder == folder);
            return left == 0
                ? null
                : new BusySource(_busy.Kind, $"{_busy.Label}: {BusySource.Plural(left, _busy.Noun)} left");
        }

        public WorkQueueCounts Counts()
        {
            var (queued, processing) = _store.CountStatuses();
            var (completed, avg) = _store.Metrics();
            return new WorkQueueCounts(
                queued, processing, avg, avg > 0 ? queued * avg : 0, completed, _store.CurrentElapsedSeconds());
        }

        private static Channel<TItem> NewChannel() =>
            Channel.CreateUnbounded<TItem>(new UnboundedChannelOptions { SingleReader = true });
    }
}
