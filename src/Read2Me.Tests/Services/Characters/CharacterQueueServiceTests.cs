using Read2Me.Core.Models;
using Read2Me.Services.Characters;
using Xunit;

namespace Read2Me.Tests.Services.Characters
{
    public class CharacterQueueServiceTests
    {
        private static readonly ProjectFolderId Folder = new("test-book");

        private static QueuedParagraph MakeItem(Guid? paragraphId = null) =>
            new(Folder, paragraphId ?? Guid.NewGuid(), "Preview", Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        private static void EnqueueAndProcess(CharacterQueueService svc, QueuedParagraph item)
        {
            svc.Enqueue([item]);
            svc.MarkProcessing(item);
        }

        [Fact]
        public void MarkFailed_RecordsOutcome()
        {
            var svc = new CharacterQueueService();
            var item = MakeItem();
            EnqueueAndProcess(svc, item);

            svc.MarkFailed(item, "some error");

            var outcome = svc.OutcomeOf(Folder, item.ParagraphId);
            Assert.NotNull(outcome);
            Assert.Equal(ParagraphOutcomeKind.Failed, outcome.Kind);
            Assert.Equal("some error", outcome.Reason);
            Assert.Null(svc.StatusOf(Folder, item.ParagraphId));
        }

        [Fact]
        public void MarkUnknown_RecordsOutcome()
        {
            var svc = new CharacterQueueService();
            var item = MakeItem();
            EnqueueAndProcess(svc, item);

            svc.MarkUnknown(item, 3.0);

            var outcome = svc.OutcomeOf(Folder, item.ParagraphId);
            Assert.NotNull(outcome);
            Assert.Equal(ParagraphOutcomeKind.Unknown, outcome.Kind);
            Assert.Null(outcome.Reason);
            Assert.Null(svc.StatusOf(Folder, item.ParagraphId));
        }

        [Fact]
        public void MarkComplete_ClearsPriorOutcome()
        {
            var svc = new CharacterQueueService();
            var paragraphId = Guid.NewGuid();
            var item = MakeItem(paragraphId);

            // Fail first round
            EnqueueAndProcess(svc, item);
            svc.MarkFailed(item, "error");
            Assert.NotNull(svc.OutcomeOf(Folder, paragraphId));

            // Re-queue and complete
            var item2 = MakeItem(paragraphId);
            EnqueueAndProcess(svc, item2);
            svc.MarkComplete(item2, 2.0);

            Assert.Null(svc.OutcomeOf(Folder, paragraphId));
        }

        [Fact]
        public void Enqueue_ClearsPriorOutcome()
        {
            var svc = new CharacterQueueService();
            var paragraphId = Guid.NewGuid();
            var item = MakeItem(paragraphId);

            EnqueueAndProcess(svc, item);
            svc.MarkFailed(item, "error");
            Assert.NotNull(svc.OutcomeOf(Folder, paragraphId));

            // Re-queue same paragraph
            var item2 = MakeItem(paragraphId);
            svc.Enqueue([item2]);

            Assert.Null(svc.OutcomeOf(Folder, paragraphId));
            Assert.Equal(ParagraphQueueStatus.Queued, svc.StatusOf(Folder, paragraphId));
        }

        [Fact]
        public void ClearOutcome_RemovesAndFiresChanged()
        {
            var svc = new CharacterQueueService();
            var item = MakeItem();
            EnqueueAndProcess(svc, item);
            svc.MarkFailed(item, "error");

            int changeCount = 0;
            svc.Changed += () => changeCount++;

            svc.ClearOutcome(Folder, item.ParagraphId);

            Assert.Null(svc.OutcomeOf(Folder, item.ParagraphId));
            Assert.Equal(1, changeCount);
        }

        [Fact]
        public void CancelAll_PreservesOutcomes()
        {
            var svc = new CharacterQueueService();
            var item = MakeItem();
            EnqueueAndProcess(svc, item);
            svc.MarkFailed(item, "error");
            Assert.NotNull(svc.OutcomeOf(Folder, item.ParagraphId));

            svc.CancelAll();

            // Outcomes survive cancel — cleared only on re-queue, manual set, or app restart.
            Assert.NotNull(svc.OutcomeOf(Folder, item.ParagraphId));
        }

        [Fact]
        public void MarkFailed_DoesNotChangeAverage()
        {
            var svc = new CharacterQueueService();

            // Complete one item to set the average
            var item1 = MakeItem();
            EnqueueAndProcess(svc, item1);
            svc.MarkComplete(item1, 10.0);
            var avgAfterSuccess = svc.Snapshot().AverageSecondsPerParagraph;
            Assert.Equal(10.0, avgAfterSuccess);

            // Fail another — average should not change
            var item2 = MakeItem();
            EnqueueAndProcess(svc, item2);
            svc.MarkFailed(item2, "error");

            Assert.Equal(10.0, svc.Snapshot().AverageSecondsPerParagraph);
        }
    }
}
