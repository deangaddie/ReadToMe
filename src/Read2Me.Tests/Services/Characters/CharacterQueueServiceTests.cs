using Read2Me.Core.Models;
using Read2Me.Services.Characters;
using Read2Me.Services.History;
using Read2Me.Services.Queueing;
using Xunit;

namespace Read2Me.Tests.Services.Characters
{
    /// <summary>
    /// What the Character Queue adds to its Work queue: the paragraph-shaped reads and the
    /// pass-throughs its processor and listeners use. The Work queue itself — every Disposition, the
    /// gate, Cancel all — is <c>WorkQueueTests</c>.
    /// </summary>
    public class CharacterQueueServiceTests
    {
        private static readonly ProjectFolderId Folder = new("test-book");

        private static QueuedParagraph MakeItem(Guid? paragraphId = null, ProjectFolderId? folder = null) =>
            new(folder ?? Folder, paragraphId ?? Guid.NewGuid(), "Preview", Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        private static void EnqueueAndProcess(CharacterQueueService svc, QueuedParagraph item)
        {
            svc.Enqueue([item]);
            svc.MarkProcessing(item);
        }

        [Fact]
        public void Key_IsTheFolderAndParagraph()
        {
            var item = MakeItem();

            Assert.Equal(new WorkItemKey(Folder, item.ParagraphId), item.Key);
        }

        [Fact]
        public void StatusOf_NarrowsToQueuedAndProcessing_AndASettledParagraphHasNone()
        {
            var svc = new CharacterQueueService(new ProjectActivity());
            var queued = MakeItem();
            var processing = MakeItem();
            var failed = MakeItem();
            svc.Enqueue([queued]);
            EnqueueAndProcess(svc, processing);
            EnqueueAndProcess(svc, failed);
            svc.Apply(failed, new Disposition.Failed("error"));

            Assert.Equal(ParagraphQueueStatus.Queued, svc.StatusOf(Folder, queued.ParagraphId));
            Assert.Equal(ParagraphQueueStatus.Processing, svc.StatusOf(Folder, processing.ParagraphId));
            Assert.Null(svc.StatusOf(Folder, failed.ParagraphId));
            Assert.Equal(new QueueOutcome(QueueOutcomeKind.Failed, "error"), svc.OutcomeOf(Folder, failed.ParagraphId));
        }

        [Fact]
        public async Task MarkDeferred_ReturnsTheParagraphToQueued_WithoutRequeuingIt()
        {
            var svc = new CharacterQueueService(new ProjectActivity());
            var item = MakeItem();
            svc.Enqueue([item]);
            svc.DrainAll(await svc.Reader.ReadAsync(TestContext.Current.CancellationToken));
            svc.MarkProcessing(item);

            svc.MarkDeferred(item);

            Assert.Equal(ParagraphQueueStatus.Queued, svc.StatusOf(Folder, item.ParagraphId));
            Assert.False(svc.Reader.TryPeek(out _));
        }

        [Fact]
        public void KnownParagraphs_AreTheFoldersTrackedAndSettledParagraphs()
        {
            var svc = new CharacterQueueService(new ProjectActivity());
            var failed = MakeItem();
            EnqueueAndProcess(svc, failed);
            svc.Apply(failed, new Disposition.Failed("error"));
            var queued = MakeItem();
            svc.Enqueue([queued, MakeItem(folder: new("another-book"))]);

            Assert.Equal(
                new[] { failed.ParagraphId, queued.ParagraphId }.Order(),
                svc.KnownParagraphs(Folder).Order());
        }

        [Fact]
        public void PendingParagraphs_GroupsTheQueuedAndProcessingParagraphsByFolder()
        {
            var svc = new CharacterQueueService(new ProjectActivity());
            var other = new ProjectFolderId("another-book");
            var a = MakeItem();
            var b = MakeItem(folder: other);
            var done = MakeItem();
            EnqueueAndProcess(svc, done);
            svc.Apply(done, new Disposition.Complete(1.0));
            svc.Enqueue([a, b]);

            var pending = svc.PendingParagraphs();

            Assert.Equal([a.ParagraphId], pending[Folder]);
            Assert.Equal([b.ParagraphId], pending[other]);
        }

        [Fact]
        public void CancelAll_RaisesCancelling_AndCancelsTheItemToken()
        {
            var svc = new CharacterQueueService(new ProjectActivity());
            var token = svc.ItemCancellationToken;
            var cancelling = 0;
            svc.Cancelling += () => cancelling++;

            svc.CancelAll();

            Assert.Equal(1, cancelling);
            Assert.True(token.IsCancellationRequested);
        }

        [Fact]
        public void Snapshot_MapsTheCounts_AndIsBusyWhileAnythingIsLeft()
        {
            var svc = new CharacterQueueService(new ProjectActivity());
            var done = MakeItem();
            EnqueueAndProcess(svc, done);
            svc.Apply(done, new Disposition.Complete(4.0));
            svc.Enqueue([MakeItem(), MakeItem()]);

            var snapshot = svc.Snapshot();

            Assert.Equal((2, 0, 1, 4.0, 8.0), (snapshot.QueuedCount, snapshot.ProcessingCount,
                snapshot.CompletedCount, snapshot.AverageSecondsPerParagraph, snapshot.EstimatedSecondsRemaining));
            Assert.True(snapshot.IsBusy);
        }
    }
}
