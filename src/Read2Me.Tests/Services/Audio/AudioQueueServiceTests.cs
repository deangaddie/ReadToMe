using Read2Me.Core.Models;
using Read2Me.Services.Audio;
using Read2Me.Services.History;
using Read2Me.Services.Queueing;
using Xunit;

namespace Read2Me.Tests.Services.Audio
{
    /// <summary>
    /// What the Audio Queue adds to its Work queue: the audio version each completion stamps, and
    /// the item-shaped reads. The Work queue itself — every Disposition, the gate, Cancel all — is
    /// <c>WorkQueueTests</c>.
    /// </summary>
    public class AudioQueueServiceTests
    {
        private static readonly ProjectFolderId Folder = new("test-book");

        private static QueuedAudioItem MakeItem(Guid? paragraphItemId = null) =>
            new(Folder, new AudioItemRef(
                paragraphItemId ?? Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()));

        private static void EnqueueAndProcess(AudioQueueService svc, QueuedAudioItem item)
        {
            svc.Enqueue([item]);
            svc.Reader.TryRead(out _);
            svc.MarkProcessing(item);
        }

        private static Guid IdOf(QueuedAudioItem item) => item.Item.ParagraphItemId;

        [Fact]
        public void Key_IsTheFolderAndParagraphItem()
        {
            var item = MakeItem();

            Assert.Equal(new WorkItemKey(Folder, IdOf(item)), item.Key);
        }

        /// <summary>
        /// Completion is one transition, not a settle followed by a side effect: the arm stamps the
        /// cache-bust version with it, so nothing can complete an item without it. The recorded audio
        /// itself crosses BookMutations, not this queue (ADR 0007).
        /// </summary>
        [Fact]
        public void Apply_Complete_StampsTheAudioVersion()
        {
            var svc = new AudioQueueService(new ProjectActivity());
            var item = MakeItem();
            EnqueueAndProcess(svc, item);

            svc.Apply(item, new Disposition.Complete(null, "audio/test.wav"));

            Assert.Null(svc.StatusOf(Folder, IdOf(item)));
            Assert.NotNull(svc.AudioVersionOf(Folder, IdOf(item)));
        }

        [Fact]
        public void Apply_SettlingWithoutCompleting_StampsNoVersion()
        {
            var svc = new AudioQueueService(new ProjectActivity());
            var failed = MakeItem();
            var unfinished = MakeItem();
            EnqueueAndProcess(svc, failed);
            svc.Apply(failed, new Disposition.Failed("network error"));
            EnqueueAndProcess(svc, unfinished);
            svc.Apply(unfinished, new Disposition.Unfinished("nothing to say", 1.5));

            Assert.Null(svc.AudioVersionOf(Folder, IdOf(failed)));
            Assert.Null(svc.AudioVersionOf(Folder, IdOf(unfinished)));
            Assert.Equal(new QueueOutcome(QueueOutcomeKind.Failed, "network error"), svc.OutcomeOf(Folder, IdOf(failed)));
        }

        [Fact]
        public void CancelAll_ClearsTheAudioVersions_AndCancelsTheItemToken()
        {
            var svc = new AudioQueueService(new ProjectActivity());
            var item = MakeItem();
            EnqueueAndProcess(svc, item);
            svc.Apply(item, new Disposition.Complete(null, "audio/test.wav"));
            var token = svc.ItemCancellationToken;

            svc.CancelAll();

            Assert.Null(svc.AudioVersionOf(Folder, IdOf(item)));
            Assert.True(token.IsCancellationRequested);
        }

        [Fact]
        public void KnownItems_IncludeACompletedItemByItsVersion()
        {
            var svc = new AudioQueueService(new ProjectActivity());
            var completed = MakeItem();
            EnqueueAndProcess(svc, completed);
            svc.Apply(completed, new Disposition.Complete(null, "audio/test.wav"));
            var queued = MakeItem();
            svc.Enqueue([queued]);

            Assert.Equal(new[] { IdOf(completed), IdOf(queued) }.Order(), svc.KnownItems(Folder).Order());
        }

        [Fact]
        public void StatusOf_NarrowsToQueuedAndProcessing()
        {
            var svc = new AudioQueueService(new ProjectActivity());
            var queued = MakeItem();
            var processing = MakeItem();
            svc.Enqueue([queued, processing]);
            svc.MarkProcessing(processing);

            Assert.Equal(AudioItemQueueStatus.Queued, svc.StatusOf(Folder, IdOf(queued)));
            Assert.Equal(AudioItemQueueStatus.Processing, svc.StatusOf(Folder, IdOf(processing)));
        }

        [Fact]
        public void Snapshot_MapsTheCounts()
        {
            var svc = new AudioQueueService(new ProjectActivity());
            var done = MakeItem();
            EnqueueAndProcess(svc, done);
            svc.Apply(done, new Disposition.Complete(3.0, "audio/done.wav"));
            var first = MakeItem();
            svc.Enqueue([first, MakeItem()]);
            svc.MarkProcessing(first);

            var snapshot = svc.Snapshot();

            Assert.Equal((1, 1, 1, 3.0, 3.0), (snapshot.QueuedCount, snapshot.ProcessingCount,
                snapshot.CompletedCount, snapshot.AverageSecondsPerItem, snapshot.EstimatedSecondsRemaining));
        }
    }
}
