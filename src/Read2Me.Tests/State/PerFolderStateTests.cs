using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using NSubstitute;
using Read2Me.App.State;
using Read2Me.Core.Models;
using Read2Me.Data.Entities;
using Read2Me.Services;
using Xunit;

namespace Read2Me.Tests.State
{
    public class PerFolderStateTests
    {
        private static readonly ProjectFolderId Folder = new("test-book");

        private static (PerFolderState state, BookHierarchyLoader loader, IProjectReader reader) Create()
        {
            var reader = Substitute.For<IProjectReader>();
            var loader = new BookHierarchyLoader(reader);
            var state = new PerFolderState(loader, Folder);
            return (state, loader, reader);
        }

        private FolderCache Cache(BookHierarchyLoader loader) => loader.For(Folder);

        private static Guid Id() => Guid.NewGuid();

        // ---------------------------------------------------------------
        // CollapseVolume
        // ---------------------------------------------------------------

        [Fact]
        public void CollapseVolume_RemovesParts_CascadesToChapters_AndParagraphs()
        {
            var (state, loader, _) = Create();
            var volId = Id();
            var partId = Id();
            var chId = Id();

            Cache(loader).SetParts(volId, [new Part { Id = partId }]);
            Cache(loader).SetChapters(partId, [new Chapter { Id = chId }]);
            Cache(loader).SetParagraphs(chId, [new Paragraph { Id = Id() }]);

            state.CollapseVolume(volId);

            Assert.Null(state.GetParts(volId));
            Assert.Null(state.GetChapters(partId));
            Assert.Null(state.GetParagraphs(chId));
        }

        [Fact]
        public void CollapseVolume_WhenNeverExpanded_IsNoOp()
        {
            var (state, _, _) = Create();
            var ex = Record.Exception(() => state.CollapseVolume(Id()));
            Assert.Null(ex);
        }

        [Fact]
        public void CollapseVolume_Twice_IsNoOp()
        {
            var (state, loader, _) = Create();
            var volId = Id();
            Cache(loader).SetParts(volId, []);

            state.CollapseVolume(volId);
            var ex = Record.Exception(() => state.CollapseVolume(volId));
            Assert.Null(ex);
        }

        // ---------------------------------------------------------------
        // CollapsePart
        // ---------------------------------------------------------------

        [Fact]
        public void CollapsePart_RemovesChapters_CascadesToParagraphs()
        {
            var (state, loader, _) = Create();
            var partId = Id();
            var chId = Id();

            Cache(loader).SetChapters(partId, [new Chapter { Id = chId }]);
            Cache(loader).SetParagraphs(chId, [new Paragraph { Id = Id() }]);

            state.CollapsePart(partId);

            Assert.Null(state.GetChapters(partId));
            Assert.Null(state.GetParagraphs(chId));
        }

        [Fact]
        public void CollapsePart_WhenNeverExpanded_IsNoOp()
        {
            var (state, _, _) = Create();
            var ex = Record.Exception(() => state.CollapsePart(Id()));
            Assert.Null(ex);
        }

        // ---------------------------------------------------------------
        // CollapseChapter
        // ---------------------------------------------------------------

        [Fact]
        public void CollapseChapter_RemovesParagraphsForThatChapter()
        {
            var (state, loader, _) = Create();
            var ch1Id = Id();
            var ch2Id = Id();

            Cache(loader).SetParagraphs(ch1Id, [new Paragraph { Id = Id() }]);
            Cache(loader).SetParagraphs(ch2Id, [new Paragraph { Id = Id() }]);

            state.CollapseChapter(ch1Id);

            Assert.Null(state.GetParagraphs(ch1Id));
            Assert.NotNull(state.GetParagraphs(ch2Id));
        }

        // ---------------------------------------------------------------
        // RemoveParagraph
        // ---------------------------------------------------------------

        [Fact]
        public void RemoveParagraph_RemovesFromContainingChapterList()
        {
            var (state, loader, _) = Create();
            var chId = Id();
            var para1Id = Id();
            var para2Id = Id();

            Cache(loader).SetParagraphs(chId, [new Paragraph { Id = para1Id }, new Paragraph { Id = para2Id }]);

            state.RemoveParagraph(para1Id);

            var remaining = state.GetParagraphs(chId);
            Assert.NotNull(remaining);
            Assert.Single(remaining);
            Assert.Equal(para2Id, remaining[0].Id);
        }

        [Fact]
        public void RemoveParagraph_WhenNotInAnyList_IsNoOp()
        {
            var (state, _, _) = Create();
            var ex = Record.Exception(() => state.RemoveParagraph(Id()));
            Assert.Null(ex);
        }

        [Fact]
        public void RemoveParagraph_ScansAllLoadedChapters()
        {
            var (state, loader, _) = Create();
            var ch1Id = Id();
            var ch2Id = Id();
            var paraId = Id();

            Cache(loader).SetParagraphs(ch1Id, [new Paragraph { Id = Id() }]);
            Cache(loader).SetParagraphs(ch2Id, [new Paragraph { Id = paraId }, new Paragraph { Id = Id() }]);

            state.RemoveParagraph(paraId);

            Assert.Single(state.GetParagraphs(ch1Id)!);
            Assert.Single(state.GetParagraphs(ch2Id)!);
        }

        // ---------------------------------------------------------------
        // GetParts / GetChapters / GetParagraphs — null for unknown id
        // ---------------------------------------------------------------

        [Fact]
        public void GetParts_UnknownId_ReturnsNull()
        {
            var (state, _, _) = Create();
            Assert.Null(state.GetParts(Id()));
        }

        [Fact]
        public void GetChapters_UnknownId_ReturnsNull()
        {
            var (state, _, _) = Create();
            Assert.Null(state.GetChapters(Id()));
        }

        [Fact]
        public void GetParagraphs_UnknownId_ReturnsNull()
        {
            var (state, _, _) = Create();
            Assert.Null(state.GetParagraphs(Id()));
        }

        // ---------------------------------------------------------------
        // Changed event
        // ---------------------------------------------------------------

        [Fact]
        public async Task OnVolumeExpanded_RaisesChangedEvent()
        {
            var (state, _, reader) = Create();
            var volId = Id();
            reader.GetPartsAsync(Folder, volId).Returns(new List<Part>());

            int fireCount = 0;
            state.Changed += () => fireCount++;

            await state.OnVolumeExpandedAsync(new Volume { Id = volId }, expanded: true);

            Assert.True(fireCount >= 1);
        }

        [Fact]
        public async Task OnVolumeCollapsed_DoesNotThrow_AndRemovesFromExpanded()
        {
            var (state, _, _) = Create();
            var volId = Id();
            state.ExpandedVolumeIds.Add(volId);

            await state.OnVolumeExpandedAsync(new Volume { Id = volId }, expanded: false);

            Assert.DoesNotContain(volId, state.ExpandedVolumeIds);
        }

        // ---------------------------------------------------------------
        // LoadingIds: true between Add and Remove
        // ---------------------------------------------------------------

        [Fact]
        public async Task LoadingIds_TrueWhileLoading()
        {
            var (state, _, reader) = Create();
            var volId = Id();

            var tcs = new TaskCompletionSource<List<Part>>();
            reader.GetPartsAsync(Folder, volId).Returns(tcs.Task);

            bool loadingDuringCallback = false;
            state.Changed += () => loadingDuringCallback = state.LoadingIds.Contains(volId);
            var loadTask = state.OnVolumeExpandedAsync(
                new Volume { Id = volId },
                expanded: true);

            // First notify (loading=true) happened synchronously before await
            Assert.True(loadingDuringCallback);

            tcs.SetResult([]);
            await loadTask;

            Assert.DoesNotContain(volId, state.LoadingIds);
        }

        // ---------------------------------------------------------------
        // RestoreExpandedAsync — single-child auto-expand
        // ---------------------------------------------------------------

        [Fact]
        public async Task RestoreExpanded_AutoExpandsSinglePart()
        {
            var (state, _, reader) = Create();
            var volId = Id();
            var partId = Id();

            state.ExpandedVolumeIds.Add(volId);
            reader.GetPartsAsync(Folder, volId).Returns(new List<Part> { new Part { Id = partId } });
            reader.GetChaptersAsync(Folder, partId).Returns(new List<Chapter>());

            await state.RestoreExpandedAsync();

            Assert.Contains(partId, state.ExpandedPartIds);
            Assert.NotNull(state.GetChapters(partId));
        }

        [Fact]
        public async Task RestoreExpanded_WithMultipleParts_OnlyExpandsTracked()
        {
            var (state, _, reader) = Create();
            var volId = Id();
            var part1Id = Id();
            var part2Id = Id();

            state.ExpandedVolumeIds.Add(volId);
            state.ExpandedPartIds.Add(part1Id);
            reader.GetPartsAsync(Folder, volId).Returns(new List<Part>
            {
                new Part { Id = part1Id },
                new Part { Id = part2Id },
            });
            reader.GetChaptersAsync(Folder, part1Id).Returns(new List<Chapter>());

            await state.RestoreExpandedAsync();

            Assert.Contains(part1Id, state.ExpandedPartIds);
            Assert.DoesNotContain(part2Id, state.ExpandedPartIds);
            Assert.NotNull(state.GetChapters(part1Id));
            Assert.Null(state.GetChapters(part2Id));
        }
    }
}
