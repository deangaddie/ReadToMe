using Read2Me.App.State;
using Read2Me.Data.Entities;
using Xunit;

namespace Read2Me.Tests.State
{
    public class FolderCacheTests
    {
        private static Guid Id() => Guid.NewGuid();

        private static Paragraph ParaWithItems(params Guid[] itemIds)
        {
            var para = new Paragraph { Id = Id() };
            foreach (var id in itemIds)
                para.Items.Add(new ParagraphItem { Id = id });
            return para;
        }

        [Fact]
        public void SetParts_thenGetParts_returnsSameList()
        {
            var cache = new FolderCache();
            var volId = Id();
            var parts = new List<Part> { new Part { Id = Id() } };

            cache.SetParts(volId, parts);

            Assert.Same(parts, cache.GetParts(volId));
        }

        [Fact]
        public void RemoveVolume_dropsParts()
        {
            var cache = new FolderCache();
            var volId = Id();
            cache.SetParts(volId, [new Part { Id = Id() }]);

            cache.RemoveVolume(volId);

            Assert.Null(cache.GetParts(volId));
        }

        // --- TryGetOwner ---

        [Fact]
        public void SetParagraphs_populatesItemOwnerIndex()
        {
            var cache = new FolderCache();
            var itemId = Id();
            var para = ParaWithItems(itemId);

            cache.SetParagraphs(Id(), [para]);

            Assert.Same(para, cache.TryGetOwner(itemId));
        }

        [Fact]
        public void TryGetOwner_unknownId_returnsNull()
        {
            var cache = new FolderCache();

            Assert.Null(cache.TryGetOwner(Id()));

            cache.SetParagraphs(Id(), [ParaWithItems(Id())]);

            Assert.Null(cache.TryGetOwner(Id()));
        }

        [Fact]
        public void RemoveChapter_prunesItemOwnerIndex()
        {
            var cache = new FolderCache();
            var chapterId = Id();
            var itemId = Id();
            cache.SetParagraphs(chapterId, [ParaWithItems(itemId)]);

            cache.RemoveChapter(chapterId);

            Assert.Null(cache.TryGetOwner(itemId));
        }

        [Fact]
        public void RemoveParagraphEverywhere_prunesItemOwnerIndexForRemovedParagraph()
        {
            var cache = new FolderCache();
            var chapterId = Id();
            var itemId1 = Id();
            var itemId2 = Id();
            var para1 = ParaWithItems(itemId1);
            var para2 = ParaWithItems(itemId2);
            cache.SetParagraphs(chapterId, [para1, para2]);

            cache.RemoveParagraphEverywhere(para1.Id);

            Assert.Null(cache.TryGetOwner(itemId1));
            Assert.Same(para2, cache.TryGetOwner(itemId2));
        }

        [Fact]
        public void TryGetOwner_multipleChapters_returnsCorrectParagraph()
        {
            var cache = new FolderCache();
            var itemId1 = Id();
            var itemId2 = Id();
            var para1 = ParaWithItems(itemId1);
            var para2 = ParaWithItems(itemId2);
            cache.SetParagraphs(Id(), [para1]);
            cache.SetParagraphs(Id(), [para2]);

            Assert.Same(para1, cache.TryGetOwner(itemId1));
            Assert.Same(para2, cache.TryGetOwner(itemId2));
        }

        [Fact]
        public void RemoveParagraphEverywhere_removesFromAllChapters()
        {
            var cache = new FolderCache();
            var ch1Id = Id();
            var ch2Id = Id();
            var paraId = Id();

            cache.SetParagraphs(ch1Id, [new Paragraph { Id = paraId }, new Paragraph { Id = Id() }]);
            cache.SetParagraphs(ch2Id, [new Paragraph { Id = paraId }, new Paragraph { Id = Id() }]);

            cache.RemoveParagraphEverywhere(paraId);

            Assert.Single(cache.GetParagraphs(ch1Id)!);
            Assert.Single(cache.GetParagraphs(ch2Id)!);
            Assert.DoesNotContain(cache.GetParagraphs(ch1Id)!, p => p.Id == paraId);
            Assert.DoesNotContain(cache.GetParagraphs(ch2Id)!, p => p.Id == paraId);
        }
    }
}
