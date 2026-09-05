using Read2Me.Core.Models;
using Read2Me.Services.Characters;
using Read2Me.Services.NodeStatus;
using Read2Me.Tests.Fakes;
using Xunit;

namespace Read2Me.Tests.Services.NodeStatus
{
    public class NodeStatusServiceTests
    {
        private static readonly ProjectFolderId Folder = new("test-book");
        private static readonly ProjectFolderId OtherFolder = new("other-book");

        private static ParagraphStatusSeedRow MakeRow(
            Guid paragraphId, Guid chapterId, Guid partId, Guid volumeId,
            int unattributed = 0) =>
            new(paragraphId, chapterId, partId, volumeId, unattributed, MissingAudio: 0, Review: 0);

        [Fact]
        public void StatusForNode_AttributionRemaining_IsParagraphGranularity_NotItemCount()
        {
            var svc = new NodeStatusService(new FakeParagraphQueueProbe());
            var vol = Guid.NewGuid();
            var part = Guid.NewGuid();
            var ch = Guid.NewGuid();
            var para = Guid.NewGuid();

            // Paragraph has 2 unattributed items → still counts as 1 paragraph.
            svc.Seed(Folder, [MakeRow(para, ch, part, vol, unattributed: 2)]);

            Assert.Equal(1, svc.StatusForNode(Folder, vol).AttributionRemaining);
            Assert.Equal(1, svc.StatusForNode(Folder, part).AttributionRemaining);
            Assert.Equal(1, svc.StatusForNode(Folder, ch).AttributionRemaining);
        }

        [Fact]
        public void StatusForNode_ZeroUnattributed_ReturnsZero()
        {
            var svc = new NodeStatusService(new FakeParagraphQueueProbe());
            var vol = Guid.NewGuid();
            var part = Guid.NewGuid();
            var ch = Guid.NewGuid();
            var para = Guid.NewGuid();

            svc.Seed(Folder, [MakeRow(para, ch, part, vol, unattributed: 0)]);

            Assert.Equal(0, svc.StatusForNode(Folder, vol).AttributionRemaining);
            Assert.Equal(0, svc.StatusForNode(Folder, part).AttributionRemaining);
            Assert.Equal(0, svc.StatusForNode(Folder, ch).AttributionRemaining);
        }

        [Fact]
        public void StatusForNode_TwoParagraphsInChapter_BothUnattributed_CountIsTwo()
        {
            var svc = new NodeStatusService(new FakeParagraphQueueProbe());
            var vol = Guid.NewGuid();
            var part = Guid.NewGuid();
            var ch = Guid.NewGuid();

            svc.Seed(Folder, [
                MakeRow(Guid.NewGuid(), ch, part, vol, unattributed: 1),
                MakeRow(Guid.NewGuid(), ch, part, vol, unattributed: 1),
            ]);

            Assert.Equal(2, svc.StatusForNode(Folder, ch).AttributionRemaining);
            Assert.Equal(2, svc.StatusForNode(Folder, part).AttributionRemaining);
            Assert.Equal(2, svc.StatusForNode(Folder, vol).AttributionRemaining);
        }

        [Fact]
        public void Clear_RemovesFolderEntries_OtherFolderSurvives()
        {
            var svc = new NodeStatusService(new FakeParagraphQueueProbe());
            var ch1 = Guid.NewGuid();
            var ch2 = Guid.NewGuid();

            svc.Seed(Folder, [MakeRow(Guid.NewGuid(), ch1, Guid.NewGuid(), Guid.NewGuid(), unattributed: 1)]);
            svc.Seed(OtherFolder, [MakeRow(Guid.NewGuid(), ch2, Guid.NewGuid(), Guid.NewGuid(), unattributed: 1)]);

            svc.Clear(Folder);

            Assert.Equal(0, svc.StatusForNode(Folder, ch1).AttributionRemaining);
            Assert.Equal(1, svc.StatusForNode(OtherFolder, ch2).AttributionRemaining);
        }

        [Fact]
        public void Seed_FiresChanged()
        {
            var svc = new NodeStatusService(new FakeParagraphQueueProbe());
            int fired = 0;
            svc.Changed += () => fired++;

            svc.Seed(Folder, []);

            Assert.Equal(1, fired);
        }

        [Fact]
        public void Clear_FiresChanged()
        {
            var svc = new NodeStatusService(new FakeParagraphQueueProbe());
            int fired = 0;
            svc.Changed += () => fired++;

            svc.Clear(Folder);

            Assert.Equal(1, fired);
        }

        [Fact]
        public void NodeStatusSummary_IsDone_WhenAllZero()
        {
            var summary = new NodeStatusSummary(0, 0, 0, AttributionProcessing: false, AttributionQueued: 0);
            Assert.True(summary.IsDone);
        }

        [Fact]
        public void NodeStatusSummary_NotDone_WhenAttributionNonZero()
        {
            var summary = new NodeStatusSummary(1, 0, 0, AttributionProcessing: false, AttributionQueued: 0);
            Assert.False(summary.IsDone);
        }

        [Fact]
        public void Paragraph_WithBothUnattributedAndMissingAudio_ContributesToBothCountsIndependently()
        {
            var svc = new NodeStatusService(new FakeParagraphQueueProbe());
            var vol = Guid.NewGuid(); var part = Guid.NewGuid(); var ch = Guid.NewGuid(); var para = Guid.NewGuid();
            svc.Seed(Folder, [new ParagraphStatusSeedRow(para, ch, part, vol, Unattributed: 1, MissingAudio: 2, Review: 0)]);

            Assert.Equal(1, svc.StatusForNode(Folder, ch).AttributionRemaining);
            Assert.Equal(1, svc.StatusForNode(Folder, ch).AudioRemaining);
        }

        // ---------------------------------------------------------------
        // OnReviewChanged
        // ---------------------------------------------------------------

        [Fact]
        public void OnReviewChanged_True_NodeReviewCountIsOne()
        {
            var svc = new NodeStatusService(new FakeParagraphQueueProbe());
            var vol = Guid.NewGuid(); var part = Guid.NewGuid(); var ch = Guid.NewGuid(); var para = Guid.NewGuid();
            svc.Seed(Folder, [new ParagraphStatusSeedRow(para, ch, part, vol, Unattributed: 0, MissingAudio: 0, Review: 1)]);

            Assert.Equal(1, svc.StatusForNode(Folder, ch).Review);
            Assert.Equal(1, svc.StatusForNode(Folder, part).Review);
            Assert.Equal(1, svc.StatusForNode(Folder, vol).Review);
        }

        [Fact]
        public void OnReviewChanged_False_NodeReviewDropsToZero()
        {
            var svc = new NodeStatusService(new FakeParagraphQueueProbe());
            var vol = Guid.NewGuid(); var part = Guid.NewGuid(); var ch = Guid.NewGuid(); var para = Guid.NewGuid();
            svc.Seed(Folder, [new ParagraphStatusSeedRow(para, ch, part, vol, Unattributed: 0, MissingAudio: 0, Review: 1)]);

            svc.OnReviewChanged(Folder, para, needsReview: false);

            Assert.Equal(0, svc.StatusForNode(Folder, ch).Review);
            Assert.Equal(0, svc.StatusForNode(Folder, part).Review);
            Assert.Equal(0, svc.StatusForNode(Folder, vol).Review);
        }

        [Fact]
        public void OnReviewChanged_True_NodeReviewIncrementsFromZero()
        {
            var svc = new NodeStatusService(new FakeParagraphQueueProbe());
            var vol = Guid.NewGuid(); var part = Guid.NewGuid(); var ch = Guid.NewGuid(); var para = Guid.NewGuid();
            svc.Seed(Folder, [new ParagraphStatusSeedRow(para, ch, part, vol, Unattributed: 0, MissingAudio: 0, Review: 0)]);

            svc.OnReviewChanged(Folder, para, needsReview: true);

            Assert.Equal(1, svc.StatusForNode(Folder, ch).Review);
        }

        [Fact]
        public void OnReviewChanged_FiresChanged()
        {
            var svc = new NodeStatusService(new FakeParagraphQueueProbe());
            var vol = Guid.NewGuid(); var part = Guid.NewGuid(); var ch = Guid.NewGuid(); var para = Guid.NewGuid();
            svc.Seed(Folder, [new ParagraphStatusSeedRow(para, ch, part, vol, Unattributed: 0, MissingAudio: 0, Review: 1)]);

            int fired = 0;
            svc.Changed += () => fired++;

            svc.OnReviewChanged(Folder, para, needsReview: false);

            Assert.Equal(1, fired);
        }

        // ---------------------------------------------------------------
        // IsDone
        // ---------------------------------------------------------------

        [Fact]
        public void IsDone_WhenAllThreeStagesZero_True()
        {
            var summary = new NodeStatusSummary(0, 0, 0, AttributionProcessing: false, AttributionQueued: 0);
            Assert.True(summary.IsDone);
        }

        [Fact]
        public void IsDone_WhenReviewNonZero_False()
        {
            var summary = new NodeStatusSummary(0, 0, 1, AttributionProcessing: false, AttributionQueued: 0);
            Assert.False(summary.IsDone);
        }

        [Fact]
        public void IsDone_IgnoresInFlightFields()
        {
            var summary = new NodeStatusSummary(0, 0, 0, AttributionProcessing: true, AttributionQueued: 5);
            Assert.True(summary.IsDone);
        }

        // ---------------------------------------------------------------
        // Queue in-flight roll-up (merged from the character queue's summary)
        // ---------------------------------------------------------------

        [Fact]
        public void StatusForNode_NothingInFlight_ByDefault()
        {
            var svc = new NodeStatusService(new FakeParagraphQueueProbe());
            var vol = Guid.NewGuid(); var part = Guid.NewGuid(); var ch = Guid.NewGuid(); var para = Guid.NewGuid();
            svc.Seed(Folder, [MakeRow(para, ch, part, vol, unattributed: 1)]);

            var s = svc.StatusForNode(Folder, ch);

            Assert.False(s.AttributionProcessing);
            Assert.Equal(0, s.AttributionQueued);
        }

        [Fact]
        public void StatusForNode_ProcessingParagraph_SetsProcessing_NotQueued()
        {
            var probe = new FakeParagraphQueueProbe();
            var svc = new NodeStatusService(probe);
            var vol = Guid.NewGuid(); var part = Guid.NewGuid(); var ch = Guid.NewGuid(); var para = Guid.NewGuid();
            svc.Seed(Folder, [MakeRow(para, ch, part, vol, unattributed: 1)]);
            probe.Set(Folder, para, ParagraphQueueStatus.Processing);

            var s = svc.StatusForNode(Folder, ch);

            Assert.True(s.AttributionProcessing);
            Assert.Equal(0, s.AttributionQueued);
        }

        [Fact]
        public void StatusForNode_QueuedParagraphs_AreCounted_AtEveryAncestor()
        {
            var probe = new FakeParagraphQueueProbe();
            var svc = new NodeStatusService(probe);
            var vol = Guid.NewGuid(); var part = Guid.NewGuid(); var ch = Guid.NewGuid();
            var p1 = Guid.NewGuid(); var p2 = Guid.NewGuid();
            svc.Seed(Folder, [MakeRow(p1, ch, part, vol), MakeRow(p2, ch, part, vol)]);
            probe.Set(Folder, p1, ParagraphQueueStatus.Queued);
            probe.Set(Folder, p2, ParagraphQueueStatus.Queued);

            Assert.Equal(2, svc.StatusForNode(Folder, ch).AttributionQueued);
            Assert.Equal(2, svc.StatusForNode(Folder, part).AttributionQueued);
            Assert.Equal(2, svc.StatusForNode(Folder, vol).AttributionQueued);
        }

        [Fact]
        public void StatusForNode_InFlightOutsideTheNode_IsNotCounted()
        {
            var probe = new FakeParagraphQueueProbe();
            var svc = new NodeStatusService(probe);
            var vol = Guid.NewGuid(); var part = Guid.NewGuid();
            var chA = Guid.NewGuid(); var chB = Guid.NewGuid();
            var inA = Guid.NewGuid(); var inB = Guid.NewGuid();
            svc.Seed(Folder, [MakeRow(inA, chA, part, vol), MakeRow(inB, chB, part, vol)]);
            probe.Set(Folder, inB, ParagraphQueueStatus.Queued);

            Assert.Equal(0, svc.StatusForNode(Folder, chA).AttributionQueued);
            Assert.Equal(1, svc.StatusForNode(Folder, chB).AttributionQueued);
        }

        [Fact]
        public void StatusForNode_InFlightInAnotherFolder_IsNotCounted()
        {
            var probe = new FakeParagraphQueueProbe();
            var svc = new NodeStatusService(probe);
            var vol = Guid.NewGuid(); var part = Guid.NewGuid(); var ch = Guid.NewGuid(); var para = Guid.NewGuid();
            svc.Seed(Folder, [MakeRow(para, ch, part, vol)]);
            probe.Set(OtherFolder, para, ParagraphQueueStatus.Queued);

            Assert.Equal(0, svc.StatusForNode(Folder, ch).AttributionQueued);
        }

        /// <summary>
        /// Pins the deliberate narrowing: ancestry lives only in the seed, so an in-flight paragraph
        /// the folder was never seeded with contributes nothing. The tree only ever renders a folder
        /// it has just seeded folder-wide, so this is unobservable in the app — but it is the
        /// behaviour, and it should fail loudly if someone reintroduces a queue-side ancestry map.
        /// </summary>
        [Fact]
        public void StatusForNode_InFlightParagraphNotSeeded_IsNotCounted()
        {
            var probe = new FakeParagraphQueueProbe();
            var svc = new NodeStatusService(probe);
            var vol = Guid.NewGuid(); var part = Guid.NewGuid(); var ch = Guid.NewGuid();
            svc.Seed(Folder, [MakeRow(Guid.NewGuid(), ch, part, vol)]);
            probe.Set(Folder, Guid.NewGuid(), ParagraphQueueStatus.Queued);

            var s = svc.StatusForNode(Folder, ch);

            Assert.Equal(0, s.AttributionQueued);
            Assert.False(s.AttributionProcessing);
        }

        [Fact]
        public void ProbeChanged_ReRaisesChanged()
        {
            var probe = new FakeParagraphQueueProbe();
            var svc = new NodeStatusService(probe);
            int fired = 0;
            svc.Changed += () => fired++;

            probe.RaiseChanged();

            Assert.Equal(1, fired);
        }
    }
}
