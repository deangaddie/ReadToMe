using System;
using System.Collections.Generic;
using Read2Me.Data.Enums;
using Read2Me.Services;
using Read2Me.Services.Books;
using Xunit;

namespace Read2Me.Tests.Services
{
    public class NarrationClassifierTests
    {
        private static readonly Guid TestNarratorId = Guid.NewGuid();

        [Fact]
        public void Classify_AssignsNarratorId_ToNarrationSegments()
        {
            var segments = new List<ParagraphSegment>
            {
                new ParagraphSegment("Hello world", SegmentType.Narration),
                new ParagraphSegment("Said the hero", SegmentType.Dialogue),
            };

            var result = NarrationClassifier.Classify(segments, TestNarratorId);

            Assert.Equal(2, result.Count);
            Assert.Equal(TestNarratorId, result[0].CharacterId);
            Assert.Equal(ParagraphItemType.Narration, result[0].ItemType);
            Assert.Null(result[1].CharacterId);
            Assert.Equal(ParagraphItemType.Character, result[1].ItemType);
        }

        [Fact]
        public void Classify_CharacterSegment_GetsNullCharacterId()
        {
            var segments = new List<ParagraphSegment>
            {
                new ParagraphSegment("Dialogue here", SegmentType.Dialogue),
            };

            var result = NarrationClassifier.Classify(segments, TestNarratorId);

            Assert.Single(result);
            Assert.Null(result[0].CharacterId);
        }

        [Fact]
        public void Classify_EmptySegments_ReturnsEmpty()
        {
            var result = NarrationClassifier.Classify([], TestNarratorId);
            Assert.Empty(result);
        }
    }
}
