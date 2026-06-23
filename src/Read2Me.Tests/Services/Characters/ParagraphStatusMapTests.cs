using Read2Me.Core.Models;
using Read2Me.Services.Characters;
using Xunit;

namespace Read2Me.Tests.Services.Characters
{
    public class ParagraphStatusMapTests
    {
        private static readonly ProjectFolderId Folder = new("test-book");

        private static ParagraphKey MakeKey(Guid? paraId = null)
            => new(Folder, paraId ?? Guid.NewGuid());

        [Fact]
        public void SummaryForNode_MatchesVolumeAncestor()
        {
            var map = new ParagraphStatusMap();
            var vol = Guid.NewGuid();
            var key = MakeKey();
            map.TryMarkQueued(key, chapter: Guid.NewGuid(), part: Guid.NewGuid(), volume: vol);

            var s = map.SummaryForNode(Folder, vol);

            Assert.Equal(1, s.QueuedCount);
            Assert.False(s.HasProcessing);
        }

        [Fact]
        public void SummaryForNode_MatchesPartAncestor()
        {
            var map = new ParagraphStatusMap();
            var part = Guid.NewGuid();
            var key = MakeKey();
            map.TryMarkQueued(key, chapter: Guid.NewGuid(), part: part, volume: Guid.NewGuid());
            map.MarkProcessing(key);

            var s = map.SummaryForNode(Folder, part);

            Assert.True(s.HasProcessing);
            Assert.Equal(0, s.QueuedCount);
        }

        [Fact]
        public void TryMarkQueued_DuplicateParagraphId_ReturnsFalse()
        {
            var map = new ParagraphStatusMap();
            var key = MakeKey();
            map.TryMarkQueued(key, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

            var second = map.TryMarkQueued(key, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

            Assert.False(second);
        }

        [Fact]
        public void ClearOutcome_RemovesOutcomeAndResolved_FiresChanged()
        {
            var map = new ParagraphStatusMap();
            var paraId = Guid.NewGuid();
            var key = MakeKey(paraId);
            map.TryMarkQueued(key, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
            map.SetOutcome(key, new ParagraphOutcome(ParagraphOutcomeKind.Failed, "err"));
            map.SetResolved(key, new ResolvedCharacter(Guid.NewGuid(), "Alice"));

            int changed = 0;
            map.Changed += () => changed++;

            map.ClearOutcome(Folder, paraId);

            Assert.Null(map.OutcomeOf(Folder, paraId));
            Assert.Null(map.ResolvedOf(Folder, paraId));
            Assert.Equal(1, changed);
        }

        [Fact]
        public void ClearOutcome_NothingToRemove_DoesNotFireChanged()
        {
            var map = new ParagraphStatusMap();
            int changed = 0;
            map.Changed += () => changed++;

            map.ClearOutcome(Folder, Guid.NewGuid());

            Assert.Equal(0, changed);
        }
    }
}
