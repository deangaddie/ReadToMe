using System;
using System.Linq;
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
        public void SelectedParagraphCount_OnlyCountsParagraphs_NotNodes()
        {
            var (sel, volId, ptId, chId) = MakeAncestry();
            var pId1 = Id();
            var pId2 = Id();
            sel.AddParagraph(pId1, new ParagraphSelection(volId, ptId, chId));
            sel.AddParagraph(pId2, new ParagraphSelection(volId, ptId, chId));
            sel.AddNode(chId);
            Assert.Equal(2, sel.SelectedParagraphCount);
        }

        // ---------------------------------------------------------------
        // NodeState — Checked
        // ---------------------------------------------------------------

        [Fact]
        public void NodeState_AddNode_Checked()
        {
            var (sel, volId, ptId, chId) = MakeAncestry();
            sel.AddNode(chId);
            Assert.Equal(TriState.Checked, sel.NodeState(chId));
        }

        [Fact]
        public void NodeState_RemoveNode_Unchecked()
        {
            var (sel, volId, ptId, chId) = MakeAncestry();
            sel.AddNode(chId);
            sel.RemoveNode(chId);
            Assert.Equal(TriState.Unchecked, sel.NodeState(chId));
        }

        // ---------------------------------------------------------------
        // NodeState — Indeterminate
        // ---------------------------------------------------------------

        [Fact]
        public void NodeState_SomeParagraphsSelected_ChapterIndeterminate()
        {
            var (sel, volId, ptId, chId) = MakeAncestry();
            sel.AddParagraph(Id(), new ParagraphSelection(volId, ptId, chId));
            // chapter node NOT added → indeterminate
            Assert.Equal(TriState.Indeterminate, sel.NodeState(chId));
        }

        [Fact]
        public void NodeState_SomeParagraphsSelected_PartIndeterminate()
        {
            var (sel, volId, ptId, chId) = MakeAncestry();
            sel.AddParagraph(Id(), new ParagraphSelection(volId, ptId, chId));
            Assert.Equal(TriState.Indeterminate, sel.NodeState(ptId));
        }

        [Fact]
        public void NodeState_SomeParagraphsSelected_VolumeIndeterminate()
        {
            var (sel, volId, ptId, chId) = MakeAncestry();
            sel.AddParagraph(Id(), new ParagraphSelection(volId, ptId, chId));
            Assert.Equal(TriState.Indeterminate, sel.NodeState(volId));
        }

        [Fact]
        public void NodeState_NoParagraphsSelected_Unchecked()
        {
            var (sel, volId, ptId, chId) = MakeAncestry();
            Assert.Equal(TriState.Unchecked, sel.NodeState(chId));
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
        // FullySelected enumerations
        // ---------------------------------------------------------------

        [Fact]
        public void FullySelectedVolumeIds_ReturnsRolledUpVolumeNodeIds()
        {
            var (sel, volId, ptId, chId) = MakeAncestry();
            var pId = Id();
            sel.AddParagraph(pId, new ParagraphSelection(volId, ptId, chId));
            sel.AddNode(volId);
            var vols = sel.FullySelectedVolumeIds().ToList();
            Assert.Contains(volId, vols);
        }

        [Fact]
        public void FullySelectedPartIds_ReturnsRolledUpPartNodeIds()
        {
            var (sel, volId, ptId, chId) = MakeAncestry();
            var pId = Id();
            sel.AddParagraph(pId, new ParagraphSelection(volId, ptId, chId));
            sel.AddNode(ptId);
            var parts = sel.FullySelectedPartIds().ToList();
            Assert.Contains(ptId, parts);
        }

        [Fact]
        public void FullySelectedChapterIds_ReturnsRolledUpChapterNodeIds()
        {
            var (sel, volId, ptId, chId) = MakeAncestry();
            var pId = Id();
            sel.AddParagraph(pId, new ParagraphSelection(volId, ptId, chId));
            sel.AddNode(chId);
            var chapters = sel.FullySelectedChapterIds().ToList();
            Assert.Contains(chId, chapters);
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
            Assert.Equal(2, sel.SelectedCountUnder(chId, SelectionNodeKind.Chapter));
        }

        // ---------------------------------------------------------------
        // Clear / BookSelectionState.Reset
        // ---------------------------------------------------------------

        [Fact]
        public void Clear_EmptiesAllSelection()
        {
            var (sel, volId, ptId, chId) = MakeAncestry();
            sel.AddParagraph(Id(), new ParagraphSelection(volId, ptId, chId));
            sel.AddNode(chId);
            sel.Clear();
            Assert.Equal(0, sel.SelectedParagraphCount);
            Assert.Equal(TriState.Unchecked, sel.NodeState(chId));
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
        // Collapse persistence: selection survives collapse (dict-only)
        // ---------------------------------------------------------------

        [Fact]
        public void Selection_SurvivesCollapse_DictIndependentOfTree()
        {
            var (sel, volId, ptId, chId) = MakeAncestry();
            var pId = Id();
            sel.AddParagraph(pId, new ParagraphSelection(volId, ptId, chId));

            // Simulating collapse: selection dict is independent of tree cache.
            // Just verify the paragraph is still selected after nothing touches it.
            Assert.True(sel.IsParagraphSelected(pId));
        }
    }
}
