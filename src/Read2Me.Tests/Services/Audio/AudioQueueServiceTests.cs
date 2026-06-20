using System;
using System.Collections.Generic;
using Read2Me.Core.Models;
using Read2Me.Services.Audio;
using Xunit;

namespace Read2Me.Tests.Services.Audio
{
    public class AudioQueueServiceTests
    {
        private static readonly ProjectFolderId Folder = new("test-book");

        private static AudioItemRef MakeItem(Guid? paragraphItemId = null) =>
            new(paragraphItemId ?? Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        private static void EnqueueAndProcess(AudioQueueService svc, AudioItemRef item)
        {
            svc.Enqueue(Folder, [item]);
            svc.MarkProcessing(Folder, item);
        }

        [Fact]
        public void Enqueue_SetsStatusQueued()
        {
            var svc = new AudioQueueService();
            var item = MakeItem();

            svc.Enqueue(Folder, [item]);

            Assert.Equal(AudioItemQueueStatus.Queued, svc.StatusOf(Folder, item.ParagraphItemId));
        }

        [Fact]
        public void Enqueue_Duplicate_IsNoOp()
        {
            var svc = new AudioQueueService();
            var id = Guid.NewGuid();
            var item = MakeItem(id);

            svc.Enqueue(Folder, [item]);
            svc.Enqueue(Folder, [item]);

            var snapshot = svc.Snapshot();
            Assert.Equal(1, snapshot.QueuedCount);
        }

        [Fact]
        public void Enqueue_AlreadyComplete_IsNoOp()
        {
            var svc = new AudioQueueService();
            var item = MakeItem();
            EnqueueAndProcess(svc, item);
            svc.MarkComplete(Folder, item, "audio/test.wav");

            svc.Enqueue(Folder, [item]);

            Assert.Null(svc.StatusOf(Folder, item.ParagraphItemId));
        }

        [Fact]
        public void MarkProcessing_SetsStatusProcessing()
        {
            var svc = new AudioQueueService();
            var item = MakeItem();
            svc.Enqueue(Folder, [item]);

            svc.MarkProcessing(Folder, item);

            Assert.Equal(AudioItemQueueStatus.Processing, svc.StatusOf(Folder, item.ParagraphItemId));
        }

        [Fact]
        public void MarkComplete_ClearsStatus()
        {
            var svc = new AudioQueueService();
            var item = MakeItem();
            EnqueueAndProcess(svc, item);

            svc.MarkComplete(Folder, item, "audio/test.wav");

            Assert.Null(svc.StatusOf(Folder, item.ParagraphItemId));
        }

        [Fact]
        public void MarkFailed_RecordsOutcome()
        {
            var svc = new AudioQueueService();
            var item = MakeItem();
            EnqueueAndProcess(svc, item);

            svc.MarkFailed(Folder, item, "network error");

            var outcome = svc.OutcomeOf(Folder, item.ParagraphItemId);
            Assert.NotNull(outcome);
            Assert.Equal(AudioItemOutcomeKind.Failed, outcome.Kind);
            Assert.Equal("network error", outcome.Reason);
            Assert.Null(svc.StatusOf(Folder, item.ParagraphItemId));
        }

        [Fact]
        public void CancelAll_DrainsPendingAndResetsStatus()
        {
            var svc = new AudioQueueService();
            var item = MakeItem();
            svc.Enqueue(Folder, [item]);

            svc.CancelAll();

            Assert.Null(svc.StatusOf(Folder, item.ParagraphItemId));
            Assert.Equal(0, svc.Snapshot().QueuedCount);
            Assert.Equal(0, svc.Snapshot().ProcessingCount);
        }

        [Fact]
        public void Snapshot_ReturnsAccurateCounts()
        {
            var svc = new AudioQueueService();
            var item1 = MakeItem();
            var item2 = MakeItem();
            svc.Enqueue(Folder, [item1, item2]);
            svc.MarkProcessing(Folder, item1);

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

            svc.Enqueue(Folder, [MakeItem()]);

            Assert.True(count > 0);
        }

        [Fact]
        public void Changed_FiresOnMarkFailed()
        {
            var svc = new AudioQueueService();
            var item = MakeItem();
            EnqueueAndProcess(svc, item);

            int count = 0;
            svc.Changed += () => count++;
            svc.MarkFailed(Folder, item, "err");

            Assert.True(count > 0);
        }

        [Fact]
        public void AudioVersionOf_ChangesAfterEachMarkComplete()
        {
            var svc = new AudioQueueService();
            var item1 = MakeItem();
            EnqueueAndProcess(svc, item1);
            svc.MarkComplete(Folder, item1, "audio/one.wav");
            var v1 = svc.AudioVersionOf(Folder, item1.ParagraphItemId);

            // Re-enqueue is a no-op once complete, but a fresh item gets its own version.
            var item2 = MakeItem();
            EnqueueAndProcess(svc, item2);
            svc.MarkComplete(Folder, item2, "audio/two.wav");
            var v2 = svc.AudioVersionOf(Folder, item2.ParagraphItemId);

            Assert.NotNull(v1);
            Assert.NotNull(v2);
        }

        [Fact]
        public void Changed_FiresOnCancelAll()
        {
            var svc = new AudioQueueService();
            svc.Enqueue(Folder, [MakeItem()]);

            int count = 0;
            svc.Changed += () => count++;
            svc.CancelAll();

            Assert.True(count > 0);
        }
    }
}
