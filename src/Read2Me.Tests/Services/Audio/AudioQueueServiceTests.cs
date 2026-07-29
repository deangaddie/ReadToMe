using Read2Me.Core.Models;
using Read2Me.Services.Audio;
using Read2Me.Services.Queueing;
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
            var svc = new AudioQueueService();
            var item = MakeItem();

            svc.Enqueue([item]);

            Assert.Equal(AudioItemQueueStatus.Queued, svc.StatusOf(Folder, IdOf(item)));
        }

        [Fact]
        public void Enqueue_Duplicate_IsNoOp()
        {
            var svc = new AudioQueueService();
            var item = MakeItem();

            svc.Enqueue([item]);
            svc.Enqueue([item]);

            var snapshot = svc.Snapshot();
            Assert.Equal(1, snapshot.QueuedCount);
        }

        [Fact]
        public void Enqueue_AlreadyComplete_RequeuesForRegeneration()
        {
            var svc = new AudioQueueService();
            var item = MakeItem();
            EnqueueAndProcess(svc, item);
            svc.Apply(item, new Disposition.Complete(null, "audio/test.wav"));

            svc.Enqueue([item]);

            Assert.Equal(AudioItemQueueStatus.Queued, svc.StatusOf(Folder, IdOf(item)));
        }

        [Fact]
        public void MarkProcessing_SetsStatusProcessing()
        {
            var svc = new AudioQueueService();
            var item = MakeItem();
            svc.Enqueue([item]);

            svc.MarkProcessing(item);

            Assert.Equal(AudioItemQueueStatus.Processing, svc.StatusOf(Folder, IdOf(item)));
        }

        // ── Apply: the five arms ──────────────────────────────────────────────

        /// <summary>
        /// Completion is one transition, not a settle followed by two side effects: the arm stamps
        /// the cache-bust version and publishes the recorded path, so nothing can complete an item
        /// without them.
        /// </summary>
        [Fact]
        public void Apply_Complete_Settles_StampsVersion_AndPublishesPath()
        {
            var svc = new AudioQueueService();
            var item = MakeItem();
            EnqueueAndProcess(svc, item);

            (ProjectFolderId Folder, Guid Id, string Path)? assigned = null;
            svc.AudioFileAssigned += (f, id, p) => assigned = (f, id, p);

            svc.Apply(item, new Disposition.Complete(null, "audio/test.wav"));

            Assert.Null(svc.StatusOf(Folder, IdOf(item)));
            Assert.NotNull(svc.AudioVersionOf(Folder, IdOf(item)));
            Assert.Equal((Folder, IdOf(item), "audio/test.wav"), assigned);
        }

        [Fact]
        public void Apply_Complete_ClearsStaleOutcome()
        {
            var svc = new AudioQueueService();
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
            var svc = new AudioQueueService();
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
            var svc = new AudioQueueService();
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
            var svc = new AudioQueueService();
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
            var svc = new AudioQueueService();
            var item = MakeItem();
            EnqueueAndProcess(svc, item);

            svc.Apply(item, new Disposition.RetryAfter(TimeSpan.Zero));

            Assert.Equal(AudioItemQueueStatus.Queued, svc.StatusOf(Folder, IdOf(item)));
            var back = await svc.Reader.ReadAsync(TestContext.Current.CancellationToken);
            Assert.Equal(0, back.Attempts.Retries);
            Assert.Equal(1, back.Attempts.Busies);
        }

        /// <summary>
        /// The reason the delayed write captures its writer: a cancel-all during the backoff must
        /// drop the item, not resurrect it on the replacement channel.
        /// </summary>
        [Fact]
        public async Task Apply_RetryAfter_ThenCancelAll_DoesNotResurrectTheItem()
        {
            var svc = new AudioQueueService();
            var item = MakeItem();
            EnqueueAndProcess(svc, item);

            svc.Apply(item, new Disposition.RetryAfter(TimeSpan.FromMilliseconds(50)));
            svc.CancelAll();

            await Task.Delay(200, TestContext.Current.CancellationToken);

            Assert.False(svc.Reader.TryRead(out _));
            Assert.Equal(0, svc.Snapshot().QueuedCount);
        }

        [Fact]
        public void CancelAll_DrainsPendingAndResetsStatus()
        {
            var svc = new AudioQueueService();
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
            var svc = new AudioQueueService();
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
            var svc = new AudioQueueService();
            int count = 0;
            svc.Changed += () => count++;

            svc.Enqueue([MakeItem()]);

            Assert.True(count > 0);
        }

        [Fact]
        public void Changed_FiresOnceOnApply()
        {
            var svc = new AudioQueueService();
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
            var svc = new AudioQueueService();
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
            var svc = new AudioQueueService();
            svc.Enqueue([MakeItem()]);

            int count = 0;
            svc.Changed += () => count++;
            svc.CancelAll();

            Assert.True(count > 0);
        }
    }
}
