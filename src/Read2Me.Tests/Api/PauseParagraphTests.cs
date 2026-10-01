using Read2Me.App.Api;
using Read2Me.Data.Entities;
using Read2Me.Data.Enums;
using Xunit;

namespace Read2Me.Tests.Api
{
    public class PauseParagraphTests
    {
        private static Paragraph ParagraphWith(ParagraphItemType? type)
        {
            var p = new Paragraph { Id = Guid.NewGuid(), Order = "a" };
            if (type.HasValue)
            {
                p.Items = new List<ParagraphItem>
                {
                    new() { Id = Guid.NewGuid(), Order = "a", ItemType = type.Value }
                };
            }
            else
            {
                p.Items = new List<ParagraphItem>();
            }
            return p;
        }

        [Theory]
        [InlineData(ParagraphItemType.VolumePause, true)]
        [InlineData(ParagraphItemType.PartPause, true)]
        [InlineData(ParagraphItemType.ChapterPause, true)]
        [InlineData(ParagraphItemType.ParagraphPause, true)]
        [InlineData(ParagraphItemType.Pause, true)]
        [InlineData(ParagraphItemType.Speech, false)]
        public void Is_ClassifiesPauseTypes(ParagraphItemType type, bool expected)
        {
            var p = ParagraphWith(type);
            Assert.Equal(expected, PauseParagraph.Is(p));
        }

        [Fact]
        public void Is_EmptyItems_ReturnsTrue()
        {
            var p = ParagraphWith(null);
            Assert.True(PauseParagraph.Is(p));
        }
    }
}
