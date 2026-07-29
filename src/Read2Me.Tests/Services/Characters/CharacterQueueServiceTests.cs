using Read2Me.Core.Models;
using Read2Me.Services.Characters;
using Read2Me.Services.Queueing;
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

        // ── DrainBatch ────────────────────────────────────────────────────────

        private static QueuedParagraph MakeChapterItem(Guid chapterId) =>
            new(Folder, Guid.NewGuid(), "Preview", chapterId, Guid.NewGuid(), Guid.NewGuid());

        [Fact]
        public async Task DrainBatch_TakesQueuedItemsFromSameChapter_UpToMax()
        {
            var svc = new CharacterQueueService();
            var chapterId = Guid.NewGuid();
            var items = Enumerable.Range(0, 4).Select(_ => MakeChapterItem(chapterId)).ToArray();
            svc.Enqueue(items);

            var first = await svc.Reader.ReadAsync();
            var batch = svc.DrainBatch(first, 3);

            Assert.Equal([items[0], items[1], items[2]], batch);
            // Fourth item still queued.
            Assert.True(svc.Reader.TryPeek(out var remaining));
            Assert.Equal(items[3].ParagraphId, remaining.ParagraphId);
        }

        [Fact]
        public async Task DrainBatch_StopsAtChapterBoundary()
        {
            var svc = new CharacterQueueService();
            var chapterA = Guid.NewGuid();
            var chapterB = Guid.NewGuid();
            var a1 = MakeChapterItem(chapterA);
            var a2 = MakeChapterItem(chapterA);
            var b1 = MakeChapterItem(chapterB);
            svc.Enqueue([a1, a2, b1]);

            var first = await svc.Reader.ReadAsync();
            var batch = svc.DrainBatch(first, 10);

            Assert.Equal([a1, a2], batch);
            Assert.True(svc.Reader.TryPeek(out var remaining));
            Assert.Equal(b1.ParagraphId, remaining.ParagraphId);
        }

        [Fact]
        public async Task DrainBatch_MaxOne_ReturnsOnlyFirst()
        {
            var svc = new CharacterQueueService();
            var chapterId = Guid.NewGuid();
            var i1 = MakeChapterItem(chapterId);
            var i2 = MakeChapterItem(chapterId);
            svc.Enqueue([i1, i2]);

            var first = await svc.Reader.ReadAsync();
            var batch = svc.DrainBatch(first, 1);

            Assert.Equal([i1], batch);
        }

        [Fact]
        public async Task DrainBatch_EmptyQueue_ReturnsFirstOnly()
        {
            var svc = new CharacterQueueService();
            var item = MakeChapterItem(Guid.NewGuid());
            svc.Enqueue([item]);

            var first = await svc.Reader.ReadAsync();
            var batch = svc.DrainBatch(first, 5);

            Assert.Equal([item], batch);
        }

        // ── DrainAll ──────────────────────────────────────────────────────────

        [Fact]
        public async Task DrainAll_ReturnsFirstPlusAllRemaining_InBookOrder_AcrossChapters()
        {
            var svc = new CharacterQueueService();
            var chapterA = Guid.NewGuid();
            var chapterB = Guid.NewGuid();
            var a1 = MakeChapterItem(chapterA);
            var a2 = MakeChapterItem(chapterA);
            var b1 = MakeChapterItem(chapterB);
            svc.Enqueue([a1, a2, b1]);

            var first = await svc.Reader.ReadAsync();
            var all = svc.DrainAll(first);

            Assert.Equal([a1, a2, b1], all);
        }

        [Fact]
        public async Task DrainAll_SpansMultipleFolders_InBookOrder()
        {
            var svc = new CharacterQueueService();
            var folderA = new ProjectFolderId("book-a");
            var folderB = new ProjectFolderId("book-b");
            var a = new QueuedParagraph(folderA, Guid.NewGuid(), "P", Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
            var b = new QueuedParagraph(folderB, Guid.NewGuid(), "P", Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
            svc.Enqueue([a, b]);

            var first = await svc.Reader.ReadAsync();
            var all = svc.DrainAll(first);

            Assert.Equal([a, b], all);
        }

        [Fact]
        public async Task DrainAll_DrainsChannel_EmptyAfterwards()
        {
            var svc = new CharacterQueueService();
            var chapterId = Guid.NewGuid();
            var items = Enumerable.Range(0, 3).Select(_ => MakeChapterItem(chapterId)).ToArray();
            svc.Enqueue(items);

            var first = await svc.Reader.ReadAsync();
            svc.DrainAll(first);

            Assert.False(svc.Reader.TryPeek(out _));
        }

        [Fact]
        public async Task DrainAll_EmptyRemainder_ReturnsFirstOnly()
        {
            var svc = new CharacterQueueService();
            var item = MakeChapterItem(Guid.NewGuid());
            svc.Enqueue([item]);

            var first = await svc.Reader.ReadAsync();
            var all = svc.DrainAll(first);

            Assert.Equal([item], all);
        }

        [Fact]
        public async Task DrainAll_MarksNothing_StatusesUnchanged()
        {
            var svc = new CharacterQueueService();
            var chapterId = Guid.NewGuid();
            var i1 = MakeChapterItem(chapterId);
            var i2 = MakeChapterItem(chapterId);
            svc.Enqueue([i1, i2]);

            var first = await svc.Reader.ReadAsync();
            svc.DrainAll(first);

            // Pure drain: statuses remain Queued (drain does not mark/resolve/requeue).
            Assert.Equal(ParagraphQueueStatus.Queued, svc.StatusOf(Folder, i1.ParagraphId));
            Assert.Equal(ParagraphQueueStatus.Queued, svc.StatusOf(Folder, i2.ParagraphId));
        }

        // ── Apply's arms ──────────────────────────────────────────────────────
        // One entry point per transition, five arms. Execution only: what each Disposition does to
        // the store and the channel. Which Disposition a given outcome earns is policy, tabled in
        // QueueDispositionTests (phase 1) and CharacterDispositionTests (phase 2).

        [Fact]
        public void Apply_Failed_RecordsOutcome()
        {
            var svc = new CharacterQueueService();
            var item = MakeItem();
            EnqueueAndProcess(svc, item);

            svc.Apply(item, new Disposition.Failed("some error"));

            var outcome = svc.OutcomeOf(Folder, item.ParagraphId);
            Assert.NotNull(outcome);
            Assert.Equal(ParagraphOutcomeKind.Failed, outcome.Kind);
            Assert.Equal("some error", outcome.Reason);
            Assert.Null(svc.StatusOf(Folder, item.ParagraphId));
        }

        [Fact]
        public void Apply_Unfinished_RecordsOutcome()
        {
            var svc = new CharacterQueueService();
            var item = MakeItem();
            EnqueueAndProcess(svc, item);

            svc.Apply(item, new Disposition.Unfinished(null, 3.0));

            var outcome = svc.OutcomeOf(Folder, item.ParagraphId);
            Assert.NotNull(outcome);
            Assert.Equal(ParagraphOutcomeKind.Unfinished, outcome.Kind);
            Assert.Null(outcome.Reason);
            Assert.Null(svc.StatusOf(Folder, item.ParagraphId));
        }

        [Fact]
        public void Apply_Unfinished_CarriesReasonIntoOutcome()
        {
            var svc = new CharacterQueueService();
            var item = MakeItem();
            EnqueueAndProcess(svc, item);

            svc.Apply(item, new Disposition.Unfinished("still unknown", 3.0));

            Assert.Equal("still unknown", svc.OutcomeOf(Folder, item.ParagraphId)!.Reason);
        }

        [Fact]
        public void Apply_Complete_ClearsPriorOutcome()
        {
            var svc = new CharacterQueueService();
            var paragraphId = Guid.NewGuid();
            var item = MakeItem(paragraphId);

            // Fail first round
            EnqueueAndProcess(svc, item);
            svc.Apply(item, new Disposition.Failed("error"));
            Assert.NotNull(svc.OutcomeOf(Folder, paragraphId));

            // Re-queue and complete
            var item2 = MakeItem(paragraphId);
            EnqueueAndProcess(svc, item2);
            svc.Apply(item2, new Disposition.Complete(2.0));

            Assert.Null(svc.OutcomeOf(Folder, paragraphId));
        }

        [Fact]
        public void Apply_RetryOnce_ReturnsToQueue_SpendingARetry()
        {
            var svc = new CharacterQueueService();
            var item = MakeItem();
            EnqueueAndProcess(svc, item);
            Assert.True(svc.Reader.TryRead(out _));

            svc.Apply(item, new Disposition.RetryOnce());

            Assert.Equal(ParagraphQueueStatus.Queued, svc.StatusOf(Folder, item.ParagraphId));
            Assert.Null(svc.OutcomeOf(Folder, item.ParagraphId));
            Assert.True(svc.Reader.TryRead(out var requeued));
            // The retry arm spends the once-only watchdog budget its own Decide arm reads — and
            // leaves the independent model-load budget untouched.
            Assert.Equal(1, requeued.Attempts.Retries);
            Assert.Equal(0, requeued.Attempts.Busies);
        }

        [Fact]
        public async Task Apply_RetryAfter_ReturnsToQueueAfterDelay_SpendingABusy()
        {
            var svc = new CharacterQueueService();
            var item = MakeItem();
            EnqueueAndProcess(svc, item);
            Assert.True(svc.Reader.TryRead(out _));

            svc.Apply(item, new Disposition.RetryAfter(TimeSpan.FromMilliseconds(50)));

            // Queued immediately; the channel write is what waits.
            Assert.Equal(ParagraphQueueStatus.Queued, svc.StatusOf(Folder, item.ParagraphId));
            Assert.False(svc.Reader.TryPeek(out _));

            var requeued = await svc.Reader.ReadAsync();
            // The model-load budget grows so the next backoff does; the once-only budget is never
            // spent, so a still-loading model retries indefinitely.
            Assert.Equal(1, requeued.Attempts.Busies);
            Assert.Equal(0, requeued.Attempts.Retries);
        }

        [Fact]
        public void Enqueue_ClearsPriorOutcome()
        {
            var svc = new CharacterQueueService();
            var paragraphId = Guid.NewGuid();
            var item = MakeItem(paragraphId);

            EnqueueAndProcess(svc, item);
            svc.Apply(item, new Disposition.Failed("error"));
            Assert.NotNull(svc.OutcomeOf(Folder, paragraphId));

            // Re-queue same paragraph
            var item2 = MakeItem(paragraphId);
            svc.Enqueue([item2]);

            Assert.Null(svc.OutcomeOf(Folder, paragraphId));
            Assert.Equal(ParagraphQueueStatus.Queued, svc.StatusOf(Folder, paragraphId));
        }

        [Fact]
        public void CancelAll_StillClearsActiveStatus()
        {
            var svc = new CharacterQueueService();
            var inFlight = MakeItem();
            EnqueueAndProcess(svc, inFlight);

            svc.CancelAll();

            Assert.Null(svc.StatusOf(Folder, inFlight.ParagraphId));
        }

        [Fact]
        public async Task Apply_RetryAfter_CancelAllDuringBackoff_DoesNotResurrectOnNewChannel()
        {
            var svc = new CharacterQueueService();
            var stale = MakeItem();
            EnqueueAndProcess(svc, stale);
            await svc.Reader.ReadAsync();

            svc.Apply(stale, new Disposition.RetryAfter(TimeSpan.FromMilliseconds(50)));

            // The channel (and its writer) is replaced while the delayed write is still pending.
            svc.CancelAll();

            // A fresh item on the *new* channel, so the assertion distinguishes "nothing arrived"
            // from "the reader simply had nothing to read yet".
            var fresh = MakeItem();
            svc.Enqueue([fresh]);

            await Task.Delay(300);

            Assert.True(svc.Reader.TryRead(out var read));
            Assert.Equal(fresh.ParagraphId, read.ParagraphId);
            Assert.False(svc.Reader.TryPeek(out _));
            Assert.Null(svc.StatusOf(Folder, stale.ParagraphId));
        }

        [Fact]
        public void ClearOutcome_RemovesAndFiresChanged()
        {
            var svc = new CharacterQueueService();
            var item = MakeItem();
            EnqueueAndProcess(svc, item);
            svc.Apply(item, new Disposition.Failed("error"));

            int changeCount = 0;
            svc.Changed += () => changeCount++;

            svc.ClearOutcome(Folder, item.ParagraphId);

            Assert.Null(svc.OutcomeOf(Folder, item.ParagraphId));
            Assert.Equal(1, changeCount);
        }

        [Fact]
        public void ClearOutcome_NothingToRemove_DoesNotFireChanged()
        {
            var svc = new CharacterQueueService();
            int changeCount = 0;
            svc.Changed += () => changeCount++;

            svc.ClearOutcome(Folder, Guid.NewGuid());

            Assert.Equal(0, changeCount);
        }

        [Fact]
        public void Enqueue_SameParagraphTwice_OnlyQueuesOnce()
        {
            var svc = new CharacterQueueService();
            var item = MakeItem();

            svc.Enqueue([item]);
            svc.Enqueue([item]);

            Assert.True(svc.Reader.TryRead(out _));
            Assert.False(svc.Reader.TryRead(out _));
            Assert.Equal(1, svc.Snapshot().QueuedCount);
        }

        [Fact]
        public void CancelAll_PreservesOutcomes()
        {
            var svc = new CharacterQueueService();
            var item = MakeItem();
            EnqueueAndProcess(svc, item);
            svc.Apply(item, new Disposition.Failed("error"));
            Assert.NotNull(svc.OutcomeOf(Folder, item.ParagraphId));

            svc.CancelAll();

            // Outcomes survive cancel — cleared only on re-queue, manual set, or app restart.
            Assert.NotNull(svc.OutcomeOf(Folder, item.ParagraphId));
        }

        [Fact]
        public void Apply_Failed_DoesNotChangeAverage()
        {
            var svc = new CharacterQueueService();

            // Complete one item to set the average
            var item1 = MakeItem();
            EnqueueAndProcess(svc, item1);
            svc.Apply(item1, new Disposition.Complete(10.0));
            var avgAfterSuccess = svc.Snapshot().AverageSecondsPerParagraph;
            Assert.Equal(10.0, avgAfterSuccess);

            // Fail another — average should not change
            var item2 = MakeItem();
            EnqueueAndProcess(svc, item2);
            svc.Apply(item2, new Disposition.Failed("error"));

            Assert.Equal(10.0, svc.Snapshot().AverageSecondsPerParagraph);
        }
    }
}
