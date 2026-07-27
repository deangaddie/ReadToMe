using Read2Me.App.State;
using Read2Me.Core.Models;
using Xunit;

namespace Read2Me.Tests.State
{
    public class BookSelectionStateTests
    {
        private static readonly ProjectFolderId Folder = new("test-book");
        private static readonly ProjectFolderId Other = new("other-book");

        private static Guid Id() => Guid.NewGuid();

        private static (FolderSelection sel, Guid volId, Guid ptId, Guid chId) MakeAncestry()
        {
            var state = new BookSelectionState();
            var sel = state.For(Folder);
            return (sel, Id(), Id(), Id());
        }

        // ---------------------------------------------------------------
        // AddParagraph / IsParagraphSelected
        // ---------------------------------------------------------------

        [Fact]
        public void AddParagraph_IsParagraphSelected_True()
        {
            var (sel, volId, ptId, chId) = MakeAncestry();
            var pId = Id();
            sel.AddParagraph(pId, new ParagraphSelection(volId, ptId, chId));
            Assert.True(sel.IsParagraphSelected(pId));
        }

        [Fact]
        public void RemoveParagraph_IsParagraphSelected_False()
        {
            var (sel, volId, ptId, chId) = MakeAncestry();
            var pId = Id();
            sel.AddParagraph(pId, new ParagraphSelection(volId, ptId, chId));
            sel.RemoveParagraph(pId);
            Assert.False(sel.IsParagraphSelected(pId));
        }

        [Fact]
        public void SelectedParagraphCount_CountsParagraphs()
        {
            var (sel, volId, ptId, chId) = MakeAncestry();
            sel.AddParagraph(Id(), new ParagraphSelection(volId, ptId, chId));
            sel.AddParagraph(Id(), new ParagraphSelection(volId, ptId, chId));
            Assert.Equal(2, sel.SelectedParagraphCount);
        }

        // ---------------------------------------------------------------
        // NodeState — derived from selected count vs total count
        // ---------------------------------------------------------------

        [Fact]
        public void NodeState_NoParagraphsSelected_Unchecked()
        {
            var (sel, volId, ptId, chId) = MakeAncestry();
            Assert.Equal(TriState.Unchecked, sel.NodeState(BookNodeLevel.Chapter, chId));
        }

        [Fact]
        public void NodeState_SomeParagraphsSelected_ChapterIndeterminate()
        {
            var (sel, volId, ptId, chId) = MakeAncestry();
            sel.SetCounts(new Dictionary<Guid, int> { [chId] = 2 });
            sel.AddParagraph(Id(), new ParagraphSelection(volId, ptId, chId));
            Assert.Equal(TriState.Indeterminate, sel.NodeState(BookNodeLevel.Chapter, chId));
        }

        [Fact]
        public void NodeState_SomeParagraphsSelected_PartIndeterminate()
        {
            var (sel, volId, ptId, chId) = MakeAncestry();
            sel.SetCounts(new Dictionary<Guid, int> { [ptId] = 2 });
            sel.AddParagraph(Id(), new ParagraphSelection(volId, ptId, chId));
            Assert.Equal(TriState.Indeterminate, sel.NodeState(BookNodeLevel.Part, ptId));
        }

        [Fact]
        public void NodeState_SomeParagraphsSelected_VolumeIndeterminate()
        {
            var (sel, volId, ptId, chId) = MakeAncestry();
            sel.SetCounts(new Dictionary<Guid, int> { [volId] = 2 });
            sel.AddParagraph(Id(), new ParagraphSelection(volId, ptId, chId));
            Assert.Equal(TriState.Indeterminate, sel.NodeState(BookNodeLevel.Volume, volId));
        }

        [Fact]
        public void NodeState_AllParagraphsSelected_ChapterChecked()
        {
            var (sel, volId, ptId, chId) = MakeAncestry();
            sel.SetCounts(new Dictionary<Guid, int> { [chId] = 1 });
            sel.AddParagraph(Id(), new ParagraphSelection(volId, ptId, chId));
            Assert.Equal(TriState.Checked, sel.NodeState(BookNodeLevel.Chapter, chId));
        }

        [Fact]
        public void NodeState_AllParagraphsSelected_VolumeChecked()
        {
            var (sel, volId, ptId, chId) = MakeAncestry();
            sel.SetCounts(new Dictionary<Guid, int> { [volId] = 2 });
            sel.AddParagraph(Id(), new ParagraphSelection(volId, ptId, chId));
            sel.AddParagraph(Id(), new ParagraphSelection(volId, ptId, chId));
            Assert.Equal(TriState.Checked, sel.NodeState(BookNodeLevel.Volume, volId));
        }

        [Fact]
        public void NodeState_CountsNotSeeded_IndeterminateWhenAnySelected()
        {
            // When count not seeded (total=0), node stays Indeterminate even with selections
            var (sel, volId, ptId, chId) = MakeAncestry();
            sel.AddParagraph(Id(), new ParagraphSelection(volId, ptId, chId));
            Assert.Equal(TriState.Indeterminate, sel.NodeState(BookNodeLevel.Chapter, chId));
        }

        // ---------------------------------------------------------------
        // SelectedCountUnder
        // ---------------------------------------------------------------

        [Fact]
        public void SelectedCountUnder_Chapter_CountsMatchingParagraphs()
        {
            var (sel, volId, ptId, chId) = MakeAncestry();
            var chId2 = Id();
            sel.AddParagraph(Id(), new ParagraphSelection(volId, ptId, chId));
            sel.AddParagraph(Id(), new ParagraphSelection(volId, ptId, chId));
            sel.AddParagraph(Id(), new ParagraphSelection(volId, ptId, chId2));
            Assert.Equal(2, sel.SelectedCountUnder(BookNodeLevel.Chapter, chId));
        }

        // ---------------------------------------------------------------
        // AddParagraphs / RemoveParagraphs (bulk)
        // ---------------------------------------------------------------

        [Fact]
        public void AddParagraphs_AddsAll()
        {
            var (sel, volId, ptId, chId) = MakeAncestry();
            var refs = new[]
            {
                new CharacterParagraphRef(Id(), chId, ptId, volId),
                new CharacterParagraphRef(Id(), chId, ptId, volId),
            };
            sel.AddParagraphs(refs);
            Assert.Equal(2, sel.SelectedParagraphCount);
        }

        [Fact]
        public void RemoveParagraphs_RemovesAll()
        {
            var (sel, volId, ptId, chId) = MakeAncestry();
            var id1 = Id();
            var id2 = Id();
            sel.AddParagraphs(new[]
            {
                new CharacterParagraphRef(id1, chId, ptId, volId),
                new CharacterParagraphRef(id2, chId, ptId, volId),
            });
            sel.RemoveParagraphs(new[] { id1, id2 });
            Assert.Equal(0, sel.SelectedParagraphCount);
        }

        // ---------------------------------------------------------------
        // Clear / BookSelectionState.Reset
        // ---------------------------------------------------------------

        [Fact]
        public void Clear_EmptiesAllSelection()
        {
            var (sel, volId, ptId, chId) = MakeAncestry();
            sel.SetCounts(new Dictionary<Guid, int> { [chId] = 1 });
            sel.AddParagraph(Id(), new ParagraphSelection(volId, ptId, chId));
            sel.Clear();
            Assert.Equal(0, sel.SelectedParagraphCount);
            Assert.Equal(TriState.Unchecked, sel.NodeState(BookNodeLevel.Chapter, chId));
        }

        [Fact]
        public void BookSelectionState_Reset_ClearsFolder()
        {
            var state = new BookSelectionState();
            var sel = state.For(Folder);
            var volId = Id(); var ptId = Id(); var chId = Id();
            sel.AddParagraph(Id(), new ParagraphSelection(volId, ptId, chId));

            state.Reset(Folder);

            Assert.Equal(0, sel.SelectedParagraphCount);
        }

        [Fact]
        public void BookSelectionState_For_DifferentFolders_Isolated()
        {
            var state = new BookSelectionState();
            var sel1 = state.For(Folder);
            var sel2 = state.For(Other);

            var volId = Id(); var ptId = Id(); var chId = Id();
            sel1.AddParagraph(Id(), new ParagraphSelection(volId, ptId, chId));

            Assert.Equal(1, sel1.SelectedParagraphCount);
            Assert.Equal(0, sel2.SelectedParagraphCount);
        }

        // ---------------------------------------------------------------
        // Collapse persistence
        // ---------------------------------------------------------------

        [Fact]
        public void Selection_SurvivesCollapse_DictIndependentOfTree()
        {
            var (sel, volId, ptId, chId) = MakeAncestry();
            var pId = Id();
            sel.AddParagraph(pId, new ParagraphSelection(volId, ptId, chId));
            Assert.True(sel.IsParagraphSelected(pId));
        }

        // ---------------------------------------------------------------
        // SetCounts + IsNodeFullySelected
        // ---------------------------------------------------------------

        [Fact]
        public void IsNodeFullySelected_WhenSelectedEqualsCount_True()
        {
            var (sel, volId, ptId, chId) = MakeAncestry();
            sel.SetCounts(new Dictionary<Guid, int> { [chId] = 1 });
            sel.AddParagraph(Id(), new ParagraphSelection(volId, ptId, chId));
            Assert.True(sel.IsNodeFullySelected(BookNodeLevel.Chapter, chId));
        }

        [Fact]
        public void IsNodeFullySelected_WhenPartiallySelected_False()
        {
            var (sel, volId, ptId, chId) = MakeAncestry();
            sel.SetCounts(new Dictionary<Guid, int> { [chId] = 2 });
            sel.AddParagraph(Id(), new ParagraphSelection(volId, ptId, chId));
            Assert.False(sel.IsNodeFullySelected(BookNodeLevel.Chapter, chId));
        }

        // ---------------------------------------------------------------
        // BulkMode
        // ---------------------------------------------------------------

        [Fact]
        public void BulkMode_DefaultsOff()
        {
            var (sel, _, _, _) = MakeAncestry();
            Assert.False(sel.BulkMode);
        }

        [Fact]
        public void BulkMode_Set_Arms()
        {
            var (sel, volId, ptId, chId) = MakeAncestry();
            sel.AddParagraph(Id(), new ParagraphSelection(volId, ptId, chId));

            sel.BulkMode = true;

            Assert.True(sel.BulkMode);
        }

        [Fact]
        public void BulkMode_ParagraphRemovedButSelectionNotEmpty_StaysArmed()
        {
            var (sel, volId, ptId, chId) = MakeAncestry();
            var first = Id();
            sel.AddParagraph(first, new ParagraphSelection(volId, ptId, chId));
            sel.AddParagraph(Id(), new ParagraphSelection(volId, ptId, chId));
            sel.BulkMode = true;

            sel.RemoveParagraph(first);

            Assert.True(sel.BulkMode);
        }

        // Unchecking rows one at a time never calls Clear(), so the disarm has to
        // hang off the inner selection's change event, not off Clear().
        [Fact]
        public void BulkMode_LastParagraphRemovedOneAtATime_Disarms()
        {
            var (sel, volId, ptId, chId) = MakeAncestry();
            var first = Id();
            var second = Id();
            sel.AddParagraph(first, new ParagraphSelection(volId, ptId, chId));
            sel.AddParagraph(second, new ParagraphSelection(volId, ptId, chId));
            sel.BulkMode = true;

            sel.RemoveParagraph(first);
            sel.RemoveParagraph(second);

            Assert.False(sel.BulkMode);
        }

        [Fact]
        public void BulkMode_Clear_Disarms()
        {
            var (sel, volId, ptId, chId) = MakeAncestry();
            sel.AddParagraph(Id(), new ParagraphSelection(volId, ptId, chId));
            sel.BulkMode = true;

            sel.Clear();

            Assert.False(sel.BulkMode);
        }

        // Emptying then re-selecting must land disarmed — the reason the flag is
        // cleared on the empty event rather than derived from the count on read.
        [Fact]
        public void BulkMode_EmptiedThenReselected_StaysDisarmed()
        {
            var (sel, volId, ptId, chId) = MakeAncestry();
            sel.AddParagraph(Id(), new ParagraphSelection(volId, ptId, chId));
            sel.BulkMode = true;
            sel.Clear();

            sel.AddParagraph(Id(), new ParagraphSelection(volId, ptId, chId));

            Assert.False(sel.BulkMode);
        }

        // No row renders against the flag, so raising OnChanged would repaint the
        // whole tree for nothing.
        [Fact]
        public void BulkMode_Set_RaisesNoOnChanged()
        {
            var (sel, volId, ptId, chId) = MakeAncestry();
            sel.AddParagraph(Id(), new ParagraphSelection(volId, ptId, chId));

            var raised = 0;
            sel.OnChanged += () => raised++;
            sel.BulkMode = true;
            sel.BulkMode = false;

            Assert.Equal(0, raised);
        }
    }
}
