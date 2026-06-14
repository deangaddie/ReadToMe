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

        private static (PerFolderState state, IProjectReader reader) Create()
        {
            var reader = Substitute.For<IProjectReader>();
            var loader = new BookHierarchyLoader(reader);
            var state = new PerFolderState(loader, Folder);
            return (state, reader);
        }

        private static Guid Id() => Guid.NewGuid();

        // ---------------------------------------------------------------
        // CollapseVolume
        // ---------------------------------------------------------------

        [Fact]
        public void CollapseVolume_RemovesParts_CascadesToChapters_AndParagraphs()
        {
            var (state, _) = Create();
            var volId = Id();
            var partId = Id();
            var chId = Id();

            var cache = state.LoadedParts; // trigger cache creation
            state.LoadedParts[volId] = [new Part { Id = partId }];
            state.LoadedChapters[partId] = [new Chapter { Id = chId }];
            state.LoadedParagraphs[chId] = [new Paragraph { Id = Id() }];

            state.CollapseVolume(volId);

            Assert.DoesNotContain(volId, state.LoadedParts.Keys);
            Assert.DoesNotContain(partId, state.LoadedChapters.Keys);
            Assert.DoesNotContain(chId, state.LoadedParagraphs.Keys);
        }

        [Fact]
        public void CollapseVolume_WhenNeverExpanded_IsNoOp()
        {
            var (state, _) = Create();
            var ex = Record.Exception(() => state.CollapseVolume(Id()));
            Assert.Null(ex);
        }

        [Fact]
        public void CollapseVolume_Twice_IsNoOp()
        {
            var (state, _) = Create();
            var volId = Id();
            state.LoadedParts[volId] = [];

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
            var (state, _) = Create();
            var partId = Id();
            var chId = Id();

            state.LoadedChapters[partId] = [new Chapter { Id = chId }];
            state.LoadedParagraphs[chId] = [new Paragraph { Id = Id() }];

            state.CollapsePart(partId);

            Assert.DoesNotContain(partId, state.LoadedChapters.Keys);
            Assert.DoesNotContain(chId, state.LoadedParagraphs.Keys);
        }

        [Fact]
        public void CollapsePart_WhenNeverExpanded_IsNoOp()
        {
            var (state, _) = Create();
            var ex = Record.Exception(() => state.CollapsePart(Id()));
            Assert.Null(ex);
        }

        // ---------------------------------------------------------------
        // CollapseChapter
        // ---------------------------------------------------------------

        [Fact]
        public void CollapseChapter_RemovesParagraphsForThatChapter()
        {
            var (state, _) = Create();
            var ch1Id = Id();
            var ch2Id = Id();

            state.LoadedParagraphs[ch1Id] = [new Paragraph { Id = Id() }];
            state.LoadedParagraphs[ch2Id] = [new Paragraph { Id = Id() }];

            state.CollapseChapter(ch1Id);

            Assert.False(state.LoadedParagraphs.ContainsKey(ch1Id));
            Assert.True(state.LoadedParagraphs.ContainsKey(ch2Id));
        }

        // ---------------------------------------------------------------
        // RemoveParagraph
        // ---------------------------------------------------------------

        [Fact]
        public void RemoveParagraph_RemovesFromContainingChapterList()
        {
            var (state, _) = Create();
            var chId = Id();
            var para1Id = Id();
            var para2Id = Id();

            state.LoadedParagraphs[chId] = [new Paragraph { Id = para1Id }, new Paragraph { Id = para2Id }];

            state.RemoveParagraph(para1Id);

            var remaining = state.GetParagraphs(chId);
            Assert.NotNull(remaining);
            Assert.Single(remaining);
            Assert.Equal(para2Id, remaining[0].Id);
        }

        [Fact]
        public void RemoveParagraph_WhenNotInAnyList_IsNoOp()
        {
            var (state, _) = Create();
            var ex = Record.Exception(() => state.RemoveParagraph(Id()));
            Assert.Null(ex);
        }

        [Fact]
        public void RemoveParagraph_ScansAllLoadedChapters()
        {
            var (state, _) = Create();
            var ch1Id = Id();
            var ch2Id = Id();
            var paraId = Id();

            state.LoadedParagraphs[ch1Id] = [new Paragraph { Id = Id() }];
            state.LoadedParagraphs[ch2Id] = [new Paragraph { Id = paraId }, new Paragraph { Id = Id() }];

            state.RemoveParagraph(paraId);

            Assert.Single(state.LoadedParagraphs[ch1Id]);
            Assert.Single(state.LoadedParagraphs[ch2Id]);
        }

        // ---------------------------------------------------------------
        // GetParts / GetChapters / GetParagraphs — null for unknown id
        // ---------------------------------------------------------------

        [Fact]
        public void GetParts_UnknownId_ReturnsNull()
        {
            var (state, _) = Create();
            Assert.Null(state.GetParts(Id()));
        }

        [Fact]
        public void GetChapters_UnknownId_ReturnsNull()
        {
            var (state, _) = Create();
            Assert.Null(state.GetChapters(Id()));
        }

        [Fact]
        public void GetParagraphs_UnknownId_ReturnsNull()
        {
            var (state, _) = Create();
            Assert.Null(state.GetParagraphs(Id()));
        }

        // ---------------------------------------------------------------
        // LoadingIds: true between Add and Remove
        // ---------------------------------------------------------------

        [Fact]
        public async Task LoadingIds_TrueWhileLoading()
        {
            var (state, reader) = Create();
            var volId = Id();

            var tcs = new TaskCompletionSource<List<Part>>();
            reader.GetPartsAsync(Folder, volId).Returns(tcs.Task);

            bool loadingDuringCallback = false;
            var loadTask = state.OnVolumeExpandedAsync(
                new Volume { Id = volId },
                expanded: true,
                notifyChanged: () => loadingDuringCallback = state.LoadingIds.Contains(volId));

            // First notify (loading=true) happened synchronously before await
            Assert.True(loadingDuringCallback);

            tcs.SetResult([]);
            await loadTask;

            Assert.DoesNotContain(volId, state.LoadingIds);
        }
    }
}
