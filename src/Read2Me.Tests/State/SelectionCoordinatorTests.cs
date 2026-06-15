using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using NSubstitute;
using Read2Me.App.State;
using Read2Me.Core.Models;
using Read2Me.Services;
using Xunit;

namespace Read2Me.Tests.State
{
    public class SelectionCoordinatorTests
    {
        private static readonly ProjectFolderId Folder = new("test-book");

        private static (SelectionCoordinator coord, FolderSelection sel, IProjectReader reader) Create()
        {
            var reader = Substitute.For<IProjectReader>();
            reader.GetVolumeCharacterParagraphsAsync(Arg.Any<ProjectFolderId>(), Arg.Any<Guid>())
                .Returns(new List<CharacterParagraphRef>());
            reader.GetPartCharacterParagraphsAsync(Arg.Any<ProjectFolderId>(), Arg.Any<Guid>())
                .Returns(new List<CharacterParagraphRef>());
            reader.GetChapterCharacterParagraphsAsync(Arg.Any<ProjectFolderId>(), Arg.Any<Guid>())
                .Returns(new List<CharacterParagraphRef>());
            reader.GetVolumeUnprocessedCharacterParagraphsAsync(Arg.Any<ProjectFolderId>(), Arg.Any<Guid>())
                .Returns(new List<CharacterParagraphRef>());
            reader.GetPartUnprocessedCharacterParagraphsAsync(Arg.Any<ProjectFolderId>(), Arg.Any<Guid>())
                .Returns(new List<CharacterParagraphRef>());
            reader.GetChapterUnprocessedCharacterParagraphsAsync(Arg.Any<ProjectFolderId>(), Arg.Any<Guid>())
                .Returns(new List<CharacterParagraphRef>());
            reader.GetVolumeCharacterParagraphCountAsync(Arg.Any<ProjectFolderId>(), Arg.Any<Guid>())
                .Returns(0);
            reader.GetPartCharacterParagraphCountAsync(Arg.Any<ProjectFolderId>(), Arg.Any<Guid>())
                .Returns(0);
            reader.GetChapterCharacterParagraphCountAsync(Arg.Any<ProjectFolderId>(), Arg.Any<Guid>())
                .Returns(0);

            return (new SelectionCoordinator(reader), new FolderSelection(), reader);
        }

        private static Guid Id() => Guid.NewGuid();

        private static CharacterParagraphRef Ref(Guid paraId, Guid chId, Guid partId, Guid volId) =>
            new CharacterParagraphRef(paraId, chId, partId, volId);

        [Fact]
        public async Task SelectParagraph_doesNotMarkChapter_whenSiblingsUnselected()
        {
            var (coord, sel, reader) = Create();
            var volId = Id(); var partId = Id(); var chId = Id();
            var para1Id = Id(); var para2Id = Id();

            // Chapter has 2 paragraphs; we select only one
            reader.GetChapterCharacterParagraphCountAsync(Folder, chId).Returns(2);
            reader.GetPartCharacterParagraphCountAsync(Folder, partId).Returns(2);
            reader.GetVolumeCharacterParagraphCountAsync(Folder, volId).Returns(2);

            await coord.ToggleParagraphAsync(sel, Folder, para1Id, chId, partId, volId, on: true);

            Assert.Equal(TriState.Indeterminate, sel.NodeState(chId));
        }

        [Fact]
        public async Task SelectParagraph_marksChapter_whenAllSiblingsSelected()
        {
            var (coord, sel, reader) = Create();
            var volId = Id(); var partId = Id(); var chId = Id();
            var para1Id = Id(); var para2Id = Id();

            // Chapter has 2 paragraphs; vol/part also 2 each
            reader.GetChapterCharacterParagraphCountAsync(Folder, chId).Returns(2);
            reader.GetPartCharacterParagraphCountAsync(Folder, partId).Returns(2);
            reader.GetVolumeCharacterParagraphCountAsync(Folder, volId).Returns(2);

            await coord.ToggleParagraphAsync(sel, Folder, para1Id, chId, partId, volId, on: true);
            await coord.ToggleParagraphAsync(sel, Folder, para2Id, chId, partId, volId, on: true);

            Assert.Equal(TriState.Checked, sel.NodeState(chId));
            Assert.Equal(TriState.Checked, sel.NodeState(partId));
            Assert.Equal(TriState.Checked, sel.NodeState(volId));
        }

        [Fact]
        public async Task SelectVolumeNode_marksAllDescendantParts_andChapters()
        {
            var (coord, sel, reader) = Create();
            var volId = Id(); var partId = Id(); var chId = Id(); var paraId = Id();

            var refs = new List<CharacterParagraphRef> { Ref(paraId, chId, partId, volId) };
            reader.GetVolumeCharacterParagraphsAsync(Folder, volId).Returns(refs);

            await coord.SetNodeAsync(sel, Folder, SelectionNodeKind.Volume, volId, on: true);

            Assert.Equal(TriState.Checked, sel.NodeState(partId));
            Assert.Equal(TriState.Checked, sel.NodeState(chId));
            Assert.True(sel.IsParagraphSelected(paraId));
        }

        [Fact]
        public async Task DeselectChapterNode_clearsAncestors()
        {
            var (coord, sel, reader) = Create();
            var volId = Id(); var partId = Id(); var chId = Id(); var paraId = Id();

            var refs = new List<CharacterParagraphRef> { Ref(paraId, chId, partId, volId) };
            reader.GetChapterCharacterParagraphsAsync(Folder, chId).Returns(refs);

            await coord.SetNodeAsync(sel, Folder, SelectionNodeKind.Chapter, chId, on: true);
            await coord.SetNodeAsync(sel, Folder, SelectionNodeKind.Chapter, chId, on: false);

            Assert.Equal(TriState.Unchecked, sel.NodeState(chId));
            Assert.Equal(TriState.Unchecked, sel.NodeState(partId));
            Assert.Equal(TriState.Unchecked, sel.NodeState(volId));
            Assert.False(sel.IsParagraphSelected(paraId));
        }

        [Fact]
        public async Task SelectNode_pullsRefs_viaReader()
        {
            var (coord, sel, reader) = Create();
            var volId = Id(); var partId = Id(); var chId = Id();
            var refs = new List<CharacterParagraphRef>
            {
                Ref(Id(), chId, partId, volId),
                Ref(Id(), chId, partId, volId),
                Ref(Id(), chId, partId, volId),
            };
            reader.GetChapterCharacterParagraphsAsync(Folder, chId).Returns(refs);

            await coord.SetNodeAsync(sel, Folder, SelectionNodeKind.Chapter, chId, on: true);

            Assert.Equal(3, sel.SelectedParagraphCount);
        }
    }
}
