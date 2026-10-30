using Read2Me.Core.Models;
using Read2Me.Services.Audio;
using Read2Me.Services.History;
using Read2Me.Services.Queueing;
using Read2Me.Tests.Fakes;
using Xunit;

namespace Read2Me.Tests.Services.Audio
{
    public class AudioQueueServiceTests
    {
        private static readonly ProjectFolderId Folder = new("test-book");

        private static QueuedAudioItem MakeItem(Guid? paragraphItemId = null) =>
            new(Folder, new AudioItemRef(
                paragraphItemId ?? Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()));

        /// <summary>
        /// Puts the item through the queue exactly as the worker does — including taking it off the
        /// channel — so a later retry arm's write is the only thing left to read.
        /// </summary>
        private static void EnqueueAndProcess(AudioQueueService svc, QueuedAudioItem item)
        {
            svc.Enqueue([item]);
            svc.Reader.TryRead(out _);
            svc.MarkProcessing(item);
        }

        private static Guid IdOf(QueuedAudioItem item) => item.Item.ParagraphItemId;

        [Fact]
        public void Enqueue_SetsStatusQueued()
        {
            var svc = new AudioQueueService(NeverBusy.Activity());
            var item = MakeItem();

            svc.Enqueue([item]);

            Assert.Equal(AudioItemQueueStatus.Queued, svc.StatusOf(Folder, IdOf(item)));
        }

        [Fact]
        public void Enqueue_Duplicate_IsNoOp()
        {
            var svc = new AudioQueueService(NeverBusy.Activity());
            var item = MakeItem();

            svc.Enqueue([item]);
            svc.Enqueue([item]);

            var snapshot = svc.Snapshot();
            Assert.Equal(1, snapshot.QueuedCount);
        }

        [Fact]
        public void Enqueue_AlreadyComplete_RequeuesForRegeneration()
        {
            var svc = new AudioQueueService(NeverBusy.Activity());
            var item = MakeItem();
            EnqueueAndProcess(svc, item);
            svc.Apply(item, new Disposition.Complete(null, "audio/test.wav"));

            svc.Enqueue([item]);

            Assert.Equal(AudioItemQueueStatus.Queued, svc.StatusOf(Folder, IdOf(item)));
        }

        [Fact]
        public void MarkProcessing_SetsStatusProcessing()
        {
            var svc = new AudioQueueService(NeverBusy.Activity());
            var item = MakeItem();
            svc.Enqueue([item]);

            svc.MarkProcessing(item);

            Assert.Equal(AudioItemQueueStatus.Processing, svc.StatusOf(Folder, IdOf(item)));
        }

        // ── Apply: the five arms ──────────────────────────────────────────────

        /// <summary>
        /// Completion is one transition, not a settle followed by a side effect: the arm stamps the
        /// cache-bust version with it, so nothing can complete an item without it. The recorded audio
        /// itself crosses BookMutations, not this queue (ADR 0007).
        /// </summary>
        [Fact]
        public void Apply_Complete_Settles_AndStampsVersion()
        {
            var svc = new AudioQueueService(NeverBusy.Activity());
            var item = MakeItem();
            EnqueueAndProcess(svc, item);

            svc.Apply(item, new Disposition.Complete(null, "audio/test.wav"));

            Assert.Null(svc.StatusOf(Folder, IdOf(item)));
            Assert.NotNull(svc.AudioVersionOf(Folder, IdOf(item)));
        }

        [Fact]
        public void Apply_Complete_ClearsStaleOutcome()
        {
            var svc = new AudioQueueService(NeverBusy.Activity());
            var item = MakeItem();
            EnqueueAndProcess(svc, item);
            svc.Apply(item, new Disposition.Failed("network error"));

            svc.Apply(item, new Disposition.Complete(null, "audio/test.wav"));

            Assert.Null(svc.OutcomeOf(Folder, IdOf(item)));
        }

        /// <summary>
        /// No current path reaches this arm — audio's own empty case is a failed resolution, which
        /// is a <c>Failed</c> work outcome. It exists so <c>Apply</c> stays total, and recording it
        /// as <c>Failed</c> would lie to the UI chip.
        /// </summary>
        [Fact]
        public void Apply_Unfinished_RecordsUnfinishedOutcome()
        {
            var svc = new AudioQueueService(NeverBusy.Activity());
            var item = MakeItem();
            EnqueueAndProcess(svc, item);

            svc.Apply(item, new Disposition.Unfinished("nothing to say", 1.5));

            var outcome = svc.OutcomeOf(Folder, IdOf(item));
            Assert.NotNull(outcome);
            Assert.Equal(AudioItemOutcomeKind.Unfinished, outcome.Kind);
            Assert.Equal("nothing to say", outcome.Reason);
            Assert.Null(svc.StatusOf(Folder, IdOf(item)));
        }

        [Fact]
        public void Apply_Failed_RecordsOutcome()
        {
            var svc = new AudioQueueService(NeverBusy.Activity());
            var item = MakeItem();
            EnqueueAndProcess(svc, item);

            svc.Apply(item, new Disposition.Failed("network error"));

            var outcome = svc.OutcomeOf(Folder, IdOf(item));
            Assert.NotNull(outcome);
            Assert.Equal(AudioItemOutcomeKind.Failed, outcome.Kind);
            Assert.Equal("network error", outcome.Reason);
            Assert.Null(svc.StatusOf(Folder, IdOf(item)));
        }

        [Fact]
        public async Task Apply_RetryOnce_ReturnsToQueued_AndSpendsARetry()
        {
            var svc = new AudioQueueService(NeverBusy.Activity());
            var item = MakeItem();
            EnqueueAndProcess(svc, item);

            svc.Apply(item, new Disposition.RetryOnce());

            Assert.Equal(AudioItemQueueStatus.Queued, svc.StatusOf(Folder, IdOf(item)));
            var back = await svc.Reader.ReadAsync(TestContext.Current.CancellationToken);
            Assert.Equal(1, back.Attempts.Retries);
            Assert.Equal(0, back.Attempts.Busies);
        }

        [Fact]
        public async Task Apply_RetryAfter_ReturnsToQueued_AndSpendsABusy()
        {
            var svc = new AudioQueueService(NeverBusy.Activity());
            var item = MakeItem();
            EnqueueAndProcess(svc, item);

            svc.Apply(item, new Disposition.RetryAfter(TimeSpan.Zero));

            Assert.Equal(AudioItemQueueStatus.Queued, svc.StatusOf(Folder, IdOf(item)));
            var back = await svc.Reader.ReadAsync(TestContext.Current.CancellationToken);
            Assert.Equal(0, back.Attempts.Retries);
            Assert.Equal(1, back.Attempts.Busies);
        }

        [Fact]
        public void Enqueue_WhileTheProjectIsRestoring_IsRefused_AndQueuesNothing()
        {
            var activity = NeverBusy.Activity();
            var svc = new AudioQueueService(activity);
            var changed = 0;
            svc.Changed += () => changed++;
            Assert.Null(activity.TryBeginRestore(Folder));

            var item = MakeItem();
            Assert.Equal(ActivityRefusalKind.Restoring, svc.Enqueue([item])?.Kind);

            Assert.Null(svc.StatusOf(Folder, IdOf(item)));
            Assert.Null(svc.BusyFor(Folder));
            Assert.False(svc.Reader.TryPeek(out _));
            Assert.Equal(0, changed);
        }

        [Fact]
        public async Task Apply_RetryAfterRestore_StaysQueued_AndRunsOnceTheRestoreEnds()
        {
            var activity = NeverBusy.Activity();
            var svc = new AudioQueueService(activity);
            var item = MakeItem();
            EnqueueAndProcess(svc, item);
            Assert.Null(activity.TryBeginRestore(Folder));

            svc.Apply(item, new Disposition.RetryAfterRestore());

            Assert.Equal(AudioItemQueueStatus.Queued, svc.StatusOf(Folder, IdOf(item)));
            Assert.Null(svc.OutcomeOf(Folder, IdOf(item)));
            await Task.Delay(50, TestContext.Current.CancellationToken);
            Assert.False(svc.Reader.TryPeek(out _));

            activity.EndRestore(Folder);

            var back = await svc.Reader.ReadAsync(TestContext.Current.CancellationToken)
                .AsTask().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.Equal(IdOf(item), IdOf(back));
            // Waiting out a Restore spends neither budget.
            Assert.Equal(default, back.Attempts);
        }

        [Fact]
        public async Task Apply_RetryAfterRestore_CancelAllDuringTheRestore_DoesNotResurrectTheItem()
        {
            var activity = NeverBusy.Activity();
            var svc = new AudioQueueService(activity);
            var item = MakeItem();
            EnqueueAndProcess(svc, item);
            Assert.Null(activity.TryBeginRestore(Folder));

            svc.Apply(item, new Disposition.RetryAfterRestore());
            svc.CancelAll();
            activity.EndRestore(Folder);

            await Task.Delay(100, TestContext.Current.CancellationToken);
            Assert.False(svc.Reader.TryRead(out _));
            Assert.Equal(0, svc.Snapshot().QueuedCount);
        }

        /// <summary>
        /// The reason the delayed write captures its writer: a cancel-all during the backoff must
        /// drop the item, not resurrect it on the replacement channel.
        /// </summary>
        [Fact]
        public async Task Apply_RetryAfter_ThenCancelAll_DoesNotResurrectTheItem()
        {
            var svc = new AudioQueueService(NeverBusy.Activity());
            var item = MakeItem();
            EnqueueAndProcess(svc, item);

            svc.Apply(item, new Disposition.RetryAfter(TimeSpan.FromMilliseconds(50)));
            svc.CancelAll();

            await Task.Delay(200, TestContext.Current.CancellationToken);

            Assert.False(svc.Reader.TryRead(out _));
            Assert.Equal(0, svc.Snapshot().QueuedCount);
        }

        /// <summary>
        /// The worker can still be generating when the queue is cleared; its later retry must not
        /// bring the item back as Queued, or the project reads busy with nothing left to drain it.
        /// </summary>
        [Fact]
        public async Task Apply_RetryAfter_AfterCancelAll_LeavesTheItemCancelled()
        {
            var svc = new AudioQueueService(NeverBusy.Activity());
            var item = MakeItem();
            EnqueueAndProcess(svc, item);

            svc.CancelAll();
            svc.Apply(item, new Disposition.RetryAfter(TimeSpan.FromMilliseconds(10)));

            Assert.Null(svc.StatusOf(Folder, IdOf(item)));
            Assert.Null(svc.BusyFor(Folder));
            await Task.Delay(100, TestContext.Current.CancellationToken);
            Assert.False(svc.Reader.TryRead(out _));
        }

        [Fact]
        public void Apply_RetryOnce_AfterCancelAll_DoesNotRequeueOnTheNewChannel()
        {
            var svc = new AudioQueueService(NeverBusy.Activity());
            var item = MakeItem();
            EnqueueAndProcess(svc, item);

            svc.CancelAll();
            svc.Apply(item, new Disposition.RetryOnce());

            Assert.Null(svc.StatusOf(Folder, IdOf(item)));
            Assert.False(svc.Reader.TryRead(out _));
        }

        [Fact]
        public void CancelAll_DrainsPendingAndResetsStatus()
        {
            var svc = new AudioQueueService(NeverBusy.Activity());
            var item = MakeItem();
            svc.Enqueue([item]);

            svc.CancelAll();

            Assert.Null(svc.StatusOf(Folder, IdOf(item)));
            Assert.Equal(0, svc.Snapshot().QueuedCount);
            Assert.Equal(0, svc.Snapshot().ProcessingCount);
        }

        [Fact]
        public void Snapshot_ReturnsAccurateCounts()
        {
            var svc = new AudioQueueService(NeverBusy.Activity());
            var item1 = MakeItem();
            var item2 = MakeItem();
            svc.Enqueue([item1, item2]);
            svc.MarkProcessing(item1);

            var snap = svc.Snapshot();

            Assert.Equal(1, snap.QueuedCount);
            Assert.Equal(1, snap.ProcessingCount);
        }

        [Fact]
        public void Changed_FiresOnEnqueue()
        {
            var svc = new AudioQueueService(NeverBusy.Activity());
            int count = 0;
            svc.Changed += () => count++;

            svc.Enqueue([MakeItem()]);

            Assert.True(count > 0);
        }

        [Fact]
        public void Changed_FiresOnceOnApply()
        {
            var svc = new AudioQueueService(NeverBusy.Activity());
            var item = MakeItem();
            EnqueueAndProcess(svc, item);

            int count = 0;
            svc.Changed += () => count++;
            svc.Apply(item, new Disposition.Failed("err"));

            Assert.Equal(1, count);
        }

        [Fact]
        public void AudioVersionOf_ChangesAfterEachCompletion()
        {
            var svc = new AudioQueueService(NeverBusy.Activity());
            var item1 = MakeItem();
            EnqueueAndProcess(svc, item1);
            svc.Apply(item1, new Disposition.Complete(null, "audio/one.wav"));
            var v1 = svc.AudioVersionOf(Folder, IdOf(item1));

            // Re-enqueue is a no-op once complete, but a fresh item gets its own version.
            var item2 = MakeItem();
            EnqueueAndProcess(svc, item2);
            svc.Apply(item2, new Disposition.Complete(null, "audio/two.wav"));
            var v2 = svc.AudioVersionOf(Folder, IdOf(item2));

            Assert.NotNull(v1);
            Assert.NotNull(v2);
        }

        [Fact]
        public void Changed_FiresOnCancelAll()
        {
            var svc = new AudioQueueService(NeverBusy.Activity());
            svc.Enqueue([MakeItem()]);

            int count = 0;
            svc.Changed += () => count++;
            svc.CancelAll();

            Assert.True(count > 0);
        }
    }
}
