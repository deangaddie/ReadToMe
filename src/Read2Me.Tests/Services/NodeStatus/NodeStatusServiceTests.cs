using System;
using Read2Me.Core.Models;
using Read2Me.Services.NodeStatus;
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
            var svc = new NodeStatusService();
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
            var svc = new NodeStatusService();
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
            var svc = new NodeStatusService();
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
            var svc = new NodeStatusService();
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
            var svc = new NodeStatusService();
            int fired = 0;
            svc.Changed += () => fired++;

            svc.Seed(Folder, []);

            Assert.Equal(1, fired);
        }

        [Fact]
        public void Clear_FiresChanged()
        {
            var svc = new NodeStatusService();
            int fired = 0;
            svc.Changed += () => fired++;

            svc.Clear(Folder);

            Assert.Equal(1, fired);
        }

        [Fact]
        public void NodeStatusSummary_IsDone_WhenAllZero()
        {
            var summary = new NodeStatusSummary(0, 0, 0);
            Assert.True(summary.IsDone);
        }

        [Fact]
        public void NodeStatusSummary_NotDone_WhenAttributionNonZero()
        {
            var summary = new NodeStatusSummary(1, 0, 0);
            Assert.False(summary.IsDone);
        }

        // ---------------------------------------------------------------
        // OnCharacterAttributed
        // ---------------------------------------------------------------

        [Fact]
        public void OnCharacterAttributed_NonLastItem_NodeCountStillOne()
        {
            var svc = new NodeStatusService();
            var vol = Guid.NewGuid(); var part = Guid.NewGuid(); var ch = Guid.NewGuid(); var para = Guid.NewGuid();
            svc.Seed(Folder, [MakeRow(para, ch, part, vol, unattributed: 2)]);

            // Assign one of two items: 1 remaining
            svc.OnCharacterAttributed(Folder, para, remainingUnattributed: 1);

            Assert.Equal(1, svc.StatusForNode(Folder, ch).AttributionRemaining);
            Assert.Equal(1, svc.StatusForNode(Folder, part).AttributionRemaining);
            Assert.Equal(1, svc.StatusForNode(Folder, vol).AttributionRemaining);
        }

        [Fact]
        public void OnCharacterAttributed_LastItem_NodeCountDropsToZero()
        {
            var svc = new NodeStatusService();
            var vol = Guid.NewGuid(); var part = Guid.NewGuid(); var ch = Guid.NewGuid(); var para = Guid.NewGuid();
            svc.Seed(Folder, [MakeRow(para, ch, part, vol, unattributed: 2)]);

            // Assign all items: 0 remaining
            svc.OnCharacterAttributed(Folder, para, remainingUnattributed: 0);

            Assert.Equal(0, svc.StatusForNode(Folder, ch).AttributionRemaining);
            Assert.Equal(0, svc.StatusForNode(Folder, part).AttributionRemaining);
            Assert.Equal(0, svc.StatusForNode(Folder, vol).AttributionRemaining);
        }

        [Fact]
        public void OnCharacterAttributed_RemainingIncreases_RaisesBadge()
        {
            // Option X: assignment semantics allow the badge to rise (e.g. un-assigning a character)
            var svc = new NodeStatusService();
            var vol = Guid.NewGuid(); var part = Guid.NewGuid(); var ch = Guid.NewGuid(); var para = Guid.NewGuid();
            svc.Seed(Folder, [MakeRow(para, ch, part, vol, unattributed: 0)]);

            svc.OnCharacterAttributed(Folder, para, remainingUnattributed: 1);

            Assert.Equal(1, svc.StatusForNode(Folder, ch).AttributionRemaining);
        }

        [Fact]
        public void OnCharacterAttributed_FiresChanged()
        {
            var svc = new NodeStatusService();
            var vol = Guid.NewGuid(); var part = Guid.NewGuid(); var ch = Guid.NewGuid(); var para = Guid.NewGuid();
            svc.Seed(Folder, [MakeRow(para, ch, part, vol, unattributed: 2)]);

            int fired = 0;
            svc.Changed += () => fired++;

            svc.OnCharacterAttributed(Folder, para, remainingUnattributed: 1);

            Assert.Equal(1, fired);
        }

        // ---------------------------------------------------------------
        // OnAudioAssigned
        // ---------------------------------------------------------------

        private static ParagraphStatusSeedRow MakeAudioRow(
            Guid paragraphId, Guid chapterId, Guid partId, Guid volumeId,
            int missingAudio) =>
            new(paragraphId, chapterId, partId, volumeId, Unattributed: 0, MissingAudio: missingAudio, Review: 0);

        [Fact]
        public void OnAudioAssigned_NonLastItem_NodeAudioCountStillOne()
        {
            var svc = new NodeStatusService();
            var vol = Guid.NewGuid(); var part = Guid.NewGuid(); var ch = Guid.NewGuid(); var para = Guid.NewGuid();
            svc.Seed(Folder, [MakeAudioRow(para, ch, part, vol, missingAudio: 2)]);

            svc.OnAudioAssigned(Folder, para);

            Assert.Equal(1, svc.StatusForNode(Folder, ch).AudioRemaining);
            Assert.Equal(1, svc.StatusForNode(Folder, part).AudioRemaining);
            Assert.Equal(1, svc.StatusForNode(Folder, vol).AudioRemaining);
        }

        [Fact]
        public void OnAudioAssigned_LastItem_NodeAudioCountDropsToZero()
        {
            var svc = new NodeStatusService();
            var vol = Guid.NewGuid(); var part = Guid.NewGuid(); var ch = Guid.NewGuid(); var para = Guid.NewGuid();
            svc.Seed(Folder, [MakeAudioRow(para, ch, part, vol, missingAudio: 2)]);

            svc.OnAudioAssigned(Folder, para);
            svc.OnAudioAssigned(Folder, para);

            Assert.Equal(0, svc.StatusForNode(Folder, ch).AudioRemaining);
            Assert.Equal(0, svc.StatusForNode(Folder, part).AudioRemaining);
            Assert.Equal(0, svc.StatusForNode(Folder, vol).AudioRemaining);
        }

        [Fact]
        public void OnAudioAssigned_ClampsAtZero_NeverNegative()
        {
            var svc = new NodeStatusService();
            var vol = Guid.NewGuid(); var part = Guid.NewGuid(); var ch = Guid.NewGuid(); var para = Guid.NewGuid();
            svc.Seed(Folder, [MakeAudioRow(para, ch, part, vol, missingAudio: 1)]);

            svc.OnAudioAssigned(Folder, para);
            svc.OnAudioAssigned(Folder, para); // extra — must not go negative

            Assert.Equal(0, svc.StatusForNode(Folder, ch).AudioRemaining);
        }

        [Fact]
        public void OnAudioAssigned_FiresChanged()
        {
            var svc = new NodeStatusService();
            var vol = Guid.NewGuid(); var part = Guid.NewGuid(); var ch = Guid.NewGuid(); var para = Guid.NewGuid();
            svc.Seed(Folder, [MakeAudioRow(para, ch, part, vol, missingAudio: 2)]);

            int fired = 0;
            svc.Changed += () => fired++;

            svc.OnAudioAssigned(Folder, para);

            Assert.Equal(1, fired);
        }

        [Fact]
        public void Paragraph_WithBothUnattributedAndMissingAudio_ContributesToBothCountsIndependently()
        {
            var svc = new NodeStatusService();
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
            var svc = new NodeStatusService();
            var vol = Guid.NewGuid(); var part = Guid.NewGuid(); var ch = Guid.NewGuid(); var para = Guid.NewGuid();
            svc.Seed(Folder, [new ParagraphStatusSeedRow(para, ch, part, vol, Unattributed: 0, MissingAudio: 0, Review: 1)]);

            Assert.Equal(1, svc.StatusForNode(Folder, ch).Review);
            Assert.Equal(1, svc.StatusForNode(Folder, part).Review);
            Assert.Equal(1, svc.StatusForNode(Folder, vol).Review);
        }

        [Fact]
        public void OnReviewChanged_False_NodeReviewDropsToZero()
        {
            var svc = new NodeStatusService();
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
            var svc = new NodeStatusService();
            var vol = Guid.NewGuid(); var part = Guid.NewGuid(); var ch = Guid.NewGuid(); var para = Guid.NewGuid();
            svc.Seed(Folder, [new ParagraphStatusSeedRow(para, ch, part, vol, Unattributed: 0, MissingAudio: 0, Review: 0)]);

            svc.OnReviewChanged(Folder, para, needsReview: true);

            Assert.Equal(1, svc.StatusForNode(Folder, ch).Review);
        }

        [Fact]
        public void OnReviewChanged_FiresChanged()
        {
            var svc = new NodeStatusService();
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
            var summary = new NodeStatusSummary(0, 0, 0);
            Assert.True(summary.IsDone);
        }

        [Fact]
        public void IsDone_WhenReviewNonZero_False()
        {
            var summary = new NodeStatusSummary(0, 0, 1);
            Assert.False(summary.IsDone);
        }
    }
}
