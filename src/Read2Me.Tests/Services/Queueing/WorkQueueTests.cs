using Read2Me.Core.Models;
using Read2Me.Services.History;
using Read2Me.Services.Queueing;
using Xunit;

namespace Read2Me.Tests.Services.Queueing
{
    /// <summary>
    /// The Work queue both queues are built on, tested once: one entry per item, the start through the
    /// Project activity gate, the executor every <see cref="Disposition"/> runs through, and Cancel all.
    /// Which Disposition an outcome earns is policy, tabled in <c>QueueDispositionTests</c> and each
    /// queue's <c>…DispositionTests</c>; what each queue adds is in its own service tests.
    /// </summary>
    public class WorkQueueTests
    {
        private static readonly ProjectFolderId Folder = new("test-book");

        private readonly record struct Work(WorkItemKey Key, AttemptState Attempts = default) : IQueuedWork<Work>
        {
            public Work WithAttempts(AttemptState attempts) => this with { Attempts = attempts };
        }

        private static Work MakeItem(Guid? id = null, ProjectFolderId? folder = null) =>
            new(new WorkItemKey(folder ?? Folder, id ?? Guid.NewGuid()));

        private static WorkQueue<Work> Sut(
            ProjectActivity? activity = null, Action<WorkItemKey>? onComplete = null, Action? onCancel = null) =>
            new(activity ?? new ProjectActivity(), new WorkQueueBusy(BusySourceKind.AudioQueue, "Audio", "item"), onComplete, onCancel);

        /// <summary>Puts the item through the queue as the worker does, taking it off the channel, so a retry's write is the only thing left to read.</summary>
        private static void EnqueueAndProcess(WorkQueue<Work> sut, Work item)
        {
            sut.Enqueue([item]);
            Assert.True(sut.Reader.TryRead(out _));
            sut.MarkProcessing(item);
        }

        // ── Apply is total ────────────────────────────────────────────────────

        public static TheoryData<Disposition> EveryDisposition() =>
        [
            new Disposition.Complete(1.0),
            new Disposition.Unfinished("left", 1.0),
            new Disposition.Failed("broke"),
            new Disposition.RetryOnce(),
            new Disposition.RetryAfter(TimeSpan.Zero),
            new Disposition.RetryAfterRestore(),
        ];

        [Fact]
        public void EveryDisposition_NamesEveryMember()
        {
            var members = typeof(Disposition).GetNestedTypes().Where(t => t.IsSubclassOf(typeof(Disposition)));
            var tabled = EveryDisposition().Select(row => row.Data.GetType());

            Assert.Equal(members.OrderBy(t => t.Name), tabled.OrderBy(t => t.Name));
        }

        [Theory]
        [MemberData(nameof(EveryDisposition))]
        public void Apply_EveryDisposition_Executes_AndAnnouncesOnce(Disposition disposition)
        {
            var sut = Sut();
            var item = MakeItem();
            EnqueueAndProcess(sut, item);
            var changed = 0;
            sut.Changed += () => changed++;

            sut.Apply(item, disposition);

            Assert.Equal(1, changed);
            Assert.NotEqual(QueueItemStatus.Processing, sut.StatusOf(item.Key));
        }

        // ── Apply's arms ──────────────────────────────────────────────────────

        [Fact]
        public void Apply_Complete_Settles_ClearsAPriorOutcome_AndRunsTheCompletionHook()
        {
            var completed = new List<WorkItemKey>();
            var sut = Sut(onComplete: completed.Add);
            var item = MakeItem();
            EnqueueAndProcess(sut, item);
            sut.Apply(item, new Disposition.Failed("first try"));
            EnqueueAndProcess(sut, item);

            sut.Apply(item, new Disposition.Complete(2.0));

            Assert.Null(sut.StatusOf(item.Key));
            Assert.Null(sut.OutcomeOf(item.Key));
            Assert.Equal([item.Key], completed);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Apply_SettlingWithoutCompleting_RecordsItsQueueOutcome_AndNoCompletion(bool unfinished)
        {
            var completed = new List<WorkItemKey>();
            var sut = Sut(onComplete: completed.Add);
            var item = MakeItem();
            EnqueueAndProcess(sut, item);

            sut.Apply(item, unfinished ? new Disposition.Unfinished("still unknown", 3.0) : new Disposition.Failed("some error"));

            var expected = unfinished
                ? new QueueOutcome(QueueOutcomeKind.Unfinished, "still unknown")
                : new QueueOutcome(QueueOutcomeKind.Failed, "some error");
            Assert.Equal(expected, sut.OutcomeOf(item.Key));
            Assert.Null(sut.StatusOf(item.Key));
            Assert.Empty(completed);
        }

        [Fact]
        public void Apply_Failed_DoesNotChangeTheAverage()
        {
            var sut = Sut();
            var done = MakeItem();
            EnqueueAndProcess(sut, done);
            sut.Apply(done, new Disposition.Complete(10.0));

            var failed = MakeItem();
            EnqueueAndProcess(sut, failed);
            sut.Apply(failed, new Disposition.Failed("error"));

            Assert.Equal(10.0, sut.Counts().AverageSeconds);
            Assert.Equal(1, sut.Counts().Completed);
        }

        [Fact]
        public void Apply_RetryOnce_ReturnsToQueue_SpendingARetry()
        {
            var sut = Sut();
            var item = MakeItem();
            EnqueueAndProcess(sut, item);

            sut.Apply(item, new Disposition.RetryOnce());

            Assert.Equal(QueueItemStatus.Queued, sut.StatusOf(item.Key));
            Assert.True(sut.Reader.TryRead(out var requeued));
            // The once-only watchdog budget its own Decide arm reads — the model-load budget untouched.
            Assert.Equal(new AttemptState(Retries: 1, Busies: 0), requeued.Attempts);
        }

        [Fact]
        public async Task Apply_RetryAfter_ReturnsToQueueAfterTheDelay_SpendingABusy()
        {
            var sut = Sut();
            var item = MakeItem();
            EnqueueAndProcess(sut, item);

            sut.Apply(item, new Disposition.RetryAfter(TimeSpan.FromMilliseconds(50)));

            // Queued immediately; the channel write is what waits.
            Assert.Equal(QueueItemStatus.Queued, sut.StatusOf(item.Key));
            Assert.False(sut.Reader.TryPeek(out _));
            var requeued = await sut.Reader.ReadAsync(TestContext.Current.CancellationToken);
            Assert.Equal(new AttemptState(Retries: 0, Busies: 1), requeued.Attempts);
        }

        [Fact]
        public async Task Apply_RetryAfterRestore_StaysQueued_AndRunsOnceTheRestoreEnds()
        {
            var activity = new ProjectActivity();
            var sut = Sut(activity);
            var item = MakeItem();
            // A Restore never begins while the item is tracked, so it begins first and the worker is
            // handed the item without the gate, as it holds one whose write the Restore refused.
            Assert.Null(activity.TryBeginRestore(Folder));
            sut.MarkProcessing(item);

            sut.Apply(item, new Disposition.RetryAfterRestore());

            Assert.Equal(QueueItemStatus.Queued, sut.StatusOf(item.Key));
            await Task.Delay(50, TestContext.Current.CancellationToken);
            Assert.False(sut.Reader.TryPeek(out _));

            activity.EndRestore(Folder);

            var requeued = await sut.Reader.ReadAsync(TestContext.Current.CancellationToken)
                .AsTask().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.Equal(item.Key, requeued.Key);
            // Waiting out a Restore spends neither budget.
            Assert.Equal(default, requeued.Attempts);
        }

        // ── Enqueue ───────────────────────────────────────────────────────────

        [Fact]
        public void Enqueue_SameItemTwice_QueuesItOnce()
        {
            var sut = Sut();
            var item = MakeItem();

            sut.Enqueue([item]);
            sut.Enqueue([item]);

            Assert.True(sut.Reader.TryRead(out _));
            Assert.False(sut.Reader.TryRead(out _));
            Assert.Equal(1, sut.Counts().Queued);
        }

        [Fact]
        public void Enqueue_ClearsAPriorQueueOutcome()
        {
            var sut = Sut();
            var item = MakeItem();
            EnqueueAndProcess(sut, item);
            sut.Apply(item, new Disposition.Failed("error"));

            sut.Enqueue([item]);

            Assert.Null(sut.OutcomeOf(item.Key));
            Assert.Equal(QueueItemStatus.Queued, sut.StatusOf(item.Key));
        }

        [Fact]
        public void Enqueue_WhileTheProjectIsRestoring_IsRefused_AndQueuesAndAnnouncesNothing()
        {
            var activity = new ProjectActivity();
            var sut = Sut(activity);
            var changed = 0;
            sut.Changed += () => changed++;
            Assert.Null(activity.TryBeginRestore(Folder));

            var item = MakeItem();
            // One refused project refuses the whole enqueue.
            Assert.Equal(ActivityRefusalKind.Restoring, sut.Enqueue([MakeItem(folder: new("another-book")), item])?.Kind);

            Assert.Empty(sut.TrackedKeys());
            Assert.Null(sut.BusyFor(Folder));
            Assert.False(sut.Reader.TryPeek(out _));
            Assert.Equal(0, changed);
        }

        [Fact]
        public void Enqueue_AnotherProjectRestoring_IsAccepted()
        {
            var activity = new ProjectActivity();
            var sut = Sut(activity);
            Assert.Null(activity.TryBeginRestore(new ProjectFolderId("another-book")));

            Assert.Null(sut.Enqueue([MakeItem()]));
        }

        // ── Drain and defer ───────────────────────────────────────────────────

        [Fact]
        public async Task DrainAll_ReturnsTheFirstPlusEveryRemainingItem_InOrder_AndMarksNothing()
        {
            var sut = Sut();
            Work[] items = [MakeItem(), MakeItem(folder: new("book-b")), MakeItem()];
            sut.Enqueue(items);

            var first = await sut.Reader.ReadAsync(TestContext.Current.CancellationToken);
            var all = sut.DrainAll(first);

            Assert.Equal(items, all);
            Assert.False(sut.Reader.TryPeek(out _));
            Assert.All(items, i => Assert.Equal(QueueItemStatus.Queued, sut.StatusOf(i.Key)));
        }

        [Fact]
        public void Defer_ReturnsToQueued_WithoutGoingBackOnTheChannel()
        {
            var sut = Sut();
            var item = MakeItem();
            EnqueueAndProcess(sut, item);

            sut.Defer(item);

            Assert.Equal(QueueItemStatus.Queued, sut.StatusOf(item.Key));
            Assert.False(sut.Reader.TryPeek(out _));
        }

        // ── Cancel all ────────────────────────────────────────────────────────

        [Fact]
        public void CancelAll_CancelsTheItemInFlight_AndRaisesCancellingBeforeAnythingClears()
        {
            var sut = Sut();
            var item = MakeItem();
            EnqueueAndProcess(sut, item);
            var token = sut.ItemCancellationToken;
            var events = new List<string>();
            sut.Cancelling += () => events.Add($"cancelling, tracked {sut.TrackedKeys().Count}");
            sut.Changed += () => events.Add($"changed, tracked {sut.TrackedKeys().Count}");

            sut.CancelAll();

            Assert.True(token.IsCancellationRequested);
            Assert.False(sut.ItemCancellationToken.IsCancellationRequested);
            Assert.Equal(["cancelling, tracked 1", "changed, tracked 0"], events);
        }

        [Fact]
        public void CancelAll_DropsWaitingItems_KeepsQueueOutcomes_AndRunsTheCancelHook()
        {
            var cancelled = 0;
            var sut = Sut(onCancel: () => cancelled++);
            var failed = MakeItem();
            EnqueueAndProcess(sut, failed);
            sut.Apply(failed, new Disposition.Failed("error"));
            var waiting = MakeItem();
            sut.Enqueue([waiting]);

            sut.CancelAll();

            Assert.Null(sut.StatusOf(waiting.Key));
            Assert.False(sut.Reader.TryPeek(out _));
            Assert.Equal(new QueueOutcome(QueueOutcomeKind.Failed, "error"), sut.OutcomeOf(failed.Key));
            Assert.Equal(1, cancelled);
        }

        /// <summary>
        /// The worker can still hold an item when the queue is cleared; its later retry or deferral
        /// must not bring it back as Queued, or the project reads busy with nothing left to drain it.
        /// </summary>
        [Theory]
        [InlineData("RetryOnce")]
        [InlineData("RetryAfter")]
        [InlineData("RetryAfterRestore")]
        [InlineData("Defer")]
        public async Task AfterCancelAll_TheItemInFlightStaysCancelled(string then)
        {
            var sut = Sut();
            var item = MakeItem();
            EnqueueAndProcess(sut, item);

            sut.CancelAll();
            switch (then)
            {
                case "RetryOnce": sut.Apply(item, new Disposition.RetryOnce()); break;
                case "RetryAfter": sut.Apply(item, new Disposition.RetryAfter(TimeSpan.FromMilliseconds(10))); break;
                case "RetryAfterRestore": sut.Apply(item, new Disposition.RetryAfterRestore()); break;
                default: sut.Defer(item); break;
            }

            Assert.Null(sut.StatusOf(item.Key));
            Assert.Null(sut.BusyFor(Folder));
            await Task.Delay(100, TestContext.Current.CancellationToken);
            Assert.False(sut.Reader.TryRead(out _));
        }

        /// <summary>
        /// The reason a delayed write captures its writer: a Cancel all while it waits drops the item
        /// rather than resurrecting it on the replacement channel.
        /// </summary>
        [Fact]
        public async Task CancelAll_DuringABackoff_DoesNotResurrectTheItemOnTheNewChannel()
        {
            var sut = Sut();
            var stale = MakeItem();
            EnqueueAndProcess(sut, stale);
            sut.Apply(stale, new Disposition.RetryAfter(TimeSpan.FromMilliseconds(50)));

            sut.CancelAll();
            // A fresh item on the new channel, so "nothing arrived" differs from "nothing to read yet".
            var fresh = MakeItem();
            sut.Enqueue([fresh]);
            await Task.Delay(300, TestContext.Current.CancellationToken);

            Assert.True(sut.Reader.TryRead(out var read));
            Assert.Equal(fresh.Key, read.Key);
            Assert.False(sut.Reader.TryPeek(out _));
            Assert.Null(sut.StatusOf(stale.Key));
        }

        [Fact]
        public async Task CancelAll_DuringARestore_DoesNotResurrectTheItem()
        {
            var activity = new ProjectActivity();
            var sut = Sut(activity);
            var item = MakeItem();
            Assert.Null(activity.TryBeginRestore(Folder));
            sut.MarkProcessing(item);
            sut.Apply(item, new Disposition.RetryAfterRestore());

            sut.CancelAll();
            activity.EndRestore(Folder);

            await Task.Delay(100, TestContext.Current.CancellationToken);
            Assert.False(sut.Reader.TryRead(out _));
            Assert.Null(sut.StatusOf(item.Key));
        }

        // ── Reads ─────────────────────────────────────────────────────────────

        [Fact]
        public void ClearOutcome_AnnouncesOnlyWhenThereWasOne()
        {
            var sut = Sut();
            var item = MakeItem();
            EnqueueAndProcess(sut, item);
            sut.Apply(item, new Disposition.Failed("error"));
            var changed = 0;
            sut.Changed += () => changed++;

            sut.ClearOutcome(item.Key);
            sut.ClearOutcome(item.Key);

            Assert.Null(sut.OutcomeOf(item.Key));
            Assert.Equal(1, changed);
        }

        [Fact]
        public void BusyFor_CountsTheFoldersQueuedAndProcessingItems_InTheQueuesOwnWords()
        {
            var sut = Sut();
            var settled = MakeItem();
            EnqueueAndProcess(sut, settled);
            sut.Apply(settled, new Disposition.Failed("error"));
            sut.Enqueue([MakeItem(), MakeItem(), MakeItem(folder: new("another-book"))]);

            Assert.Equal(new BusySource(BusySourceKind.AudioQueue, "Audio: 2 items left"), sut.BusyFor(Folder));
        }

        [Fact]
        public void KnownKeys_AreTheTrackedItemsAndThoseWithAQueueOutcome()
        {
            var sut = Sut();
            var completed = MakeItem();
            EnqueueAndProcess(sut, completed);
            sut.Apply(completed, new Disposition.Complete(1.0));
            var failed = MakeItem();
            EnqueueAndProcess(sut, failed);
            sut.Apply(failed, new Disposition.Failed("error"));
            var queued = MakeItem();
            sut.Enqueue([queued]);

            Assert.Equal(new[] { failed.Key, queued.Key }.OrderBy(k => k.Id), sut.KnownKeys().OrderBy(k => k.Id));
        }

        [Fact]
        public void Counts_ReadQueuedAndProcessing()
        {
            var sut = Sut();
            var first = MakeItem();
            sut.Enqueue([first, MakeItem()]);
            sut.MarkProcessing(first);

            var counts = sut.Counts();

            Assert.Equal((1, 1), (counts.Queued, counts.Processing));
        }
    }
}
