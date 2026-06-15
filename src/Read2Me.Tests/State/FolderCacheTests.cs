using System;
using System.Collections.Generic;
using Read2Me.App.State;
using Read2Me.Data.Entities;
using Xunit;

namespace Read2Me.Tests.State
{
    public class FolderCacheTests
    {
        private static Guid Id() => Guid.NewGuid();

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
