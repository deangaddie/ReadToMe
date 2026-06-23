using Read2Me.App.State;
using Read2Me.Core.Models;
using Xunit;

namespace Read2Me.Tests.State
{
    public class RollupSelectionTests
    {
        private readonly record struct FakeItem(
            Guid VolumeId, Guid PartId, Guid ChapterId, Guid SelectionKey) : IHasNodeAncestry;

        private static Guid Id() => Guid.NewGuid();

        private static FakeItem Item(Guid vol, Guid pt, Guid ch) =>
            new(vol, pt, ch, Id());

        // ---------------------------------------------------------------
        // Tri-state derivation
        // ---------------------------------------------------------------

        [Fact]
        public void NodeState_Empty_Unchecked()
        {
            var sel = new RollupSelection<FakeItem>();
            Assert.Equal(TriState.Unchecked, sel.NodeState(BookNodeLevel.Chapter, Id()));
        }

        [Fact]
        public void NodeState_SomeButNotAll_Indeterminate()
        {
            var sel = new RollupSelection<FakeItem>();
            var vol = Id(); var pt = Id(); var ch = Id();
            sel.SetCounts(new Dictionary<Guid, int> { [ch] = 2 });
            sel.Add(Item(vol, pt, ch));
            Assert.Equal(TriState.Indeterminate, sel.NodeState(BookNodeLevel.Chapter, ch));
        }

        [Fact]
        public void NodeState_AllSelected_Checked()
        {
            var sel = new RollupSelection<FakeItem>();
            var vol = Id(); var pt = Id(); var ch = Id();
            sel.SetCounts(new Dictionary<Guid, int> { [ch] = 2 });
            sel.Add(Item(vol, pt, ch));
            sel.Add(Item(vol, pt, ch));
            Assert.Equal(TriState.Checked, sel.NodeState(BookNodeLevel.Chapter, ch));
        }

        [Fact]
        public void NodeState_CountZero_NeverFalseChecked_StaysIndeterminate()
        {
            var sel = new RollupSelection<FakeItem>();
            var vol = Id(); var pt = Id(); var ch = Id();
            sel.SetCounts(new Dictionary<Guid, int> { [ch] = 0 });
            sel.Add(Item(vol, pt, ch));
            Assert.Equal(TriState.Indeterminate, sel.NodeState(BookNodeLevel.Chapter, ch));
        }

        [Fact]
        public void NodeState_CountsNotSeeded_StaysIndeterminate()
        {
            var sel = new RollupSelection<FakeItem>();
            var vol = Id(); var pt = Id(); var ch = Id();
            sel.Add(Item(vol, pt, ch));
            Assert.Equal(TriState.Indeterminate, sel.NodeState(BookNodeLevel.Chapter, ch));
        }

        // ---------------------------------------------------------------
        // Roll-up by level — counts the right ancestry field
        // ---------------------------------------------------------------

        [Fact]
        public void SelectedCountUnder_CountsCorrectAncestryFieldPerLevel()
        {
            var sel = new RollupSelection<FakeItem>();
            var vol = Id(); var pt = Id(); var ch = Id();
            var otherCh = Id();
            sel.Add(Item(vol, pt, ch));
            sel.Add(Item(vol, pt, ch));
            sel.Add(Item(vol, pt, otherCh));

            Assert.Equal(2, sel.SelectedCountUnder(BookNodeLevel.Chapter, ch));
            Assert.Equal(3, sel.SelectedCountUnder(BookNodeLevel.Part, pt));
            Assert.Equal(3, sel.SelectedCountUnder(BookNodeLevel.Volume, vol));
        }

        // ---------------------------------------------------------------
        // OnChanged — raised exactly once per mutation
        // ---------------------------------------------------------------

        [Fact]
        public void Add_RaisesOnChangedOnce()
        {
            var sel = new RollupSelection<FakeItem>();
            var count = 0;
            sel.OnChanged += () => count++;
            sel.Add(Item(Id(), Id(), Id()));
            Assert.Equal(1, count);
        }

        [Fact]
        public void Remove_RaisesOnChangedOnce_WhenPresent()
        {
            var sel = new RollupSelection<FakeItem>();
            var item = Item(Id(), Id(), Id());
            sel.Add(item);
            var count = 0;
            sel.OnChanged += () => count++;
            sel.Remove(item.SelectionKey);
            Assert.Equal(1, count);
        }

        [Fact]
        public void Remove_DoesNotRaise_WhenAbsent()
        {
            var sel = new RollupSelection<FakeItem>();
            var count = 0;
            sel.OnChanged += () => count++;
            sel.Remove(Id());
            Assert.Equal(0, count);
        }

        [Fact]
        public void SetCounts_RaisesOnChangedOnce()
        {
            var sel = new RollupSelection<FakeItem>();
            var count = 0;
            sel.OnChanged += () => count++;
            sel.SetCounts(new Dictionary<Guid, int>());
            Assert.Equal(1, count);
        }

        // ---------------------------------------------------------------
        // Basic membership / lookup
        // ---------------------------------------------------------------

        [Fact]
        public void Add_IsSelected_True()
        {
            var sel = new RollupSelection<FakeItem>();
            var item = Item(Id(), Id(), Id());
            sel.Add(item);
            Assert.True(sel.IsSelected(item.SelectionKey));
        }

        [Fact]
        public void TryGet_ReturnsStoredItem()
        {
            var sel = new RollupSelection<FakeItem>();
            var item = Item(Id(), Id(), Id());
            sel.Add(item);
            Assert.True(sel.TryGet(item.SelectionKey, out var got));
            Assert.Equal(item, got);
        }

        [Fact]
        public void TryGet_Absent_False()
        {
            var sel = new RollupSelection<FakeItem>();
            Assert.False(sel.TryGet(Id(), out _));
        }
    }
}
