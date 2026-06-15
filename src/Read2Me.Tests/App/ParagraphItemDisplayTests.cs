using System;
using System.Collections.Generic;
using Read2Me.App.Shared;
using Read2Me.Data.Entities;
using Read2Me.Data.Enums;
using Xunit;

namespace Read2Me.Tests.App
{
    public class ParagraphItemDisplayTests
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
        [InlineData(ParagraphItemType.Narration, false)]
        [InlineData(ParagraphItemType.Character, false)]
        public void IsPauseParagraph_ClassifiesPauseTypes(ParagraphItemType type, bool expected)
        {
            var p = ParagraphWith(type);
            Assert.Equal(expected, ParagraphItemDisplay.IsPauseParagraph(p));
        }

        [Fact]
        public void IsPauseParagraph_EmptyItems_ReturnsTrue()
        {
            var p = ParagraphWith(null);
            Assert.True(ParagraphItemDisplay.IsPauseParagraph(p));
        }

        [Theory]
        [InlineData(ParagraphItemType.VolumePause)]
        [InlineData(ParagraphItemType.PartPause)]
        [InlineData(ParagraphItemType.ChapterPause)]
        [InlineData(ParagraphItemType.ParagraphPause)]
        [InlineData(ParagraphItemType.Pause)]
        public void GetPauseLabel_ReturnsNonEmptyLabel(ParagraphItemType type)
        {
            Assert.False(string.IsNullOrWhiteSpace(ParagraphItemDisplay.GetPauseLabel(type)));
        }

        [Fact]
        public void GetPauseLabel_Null_ReturnsFallback()
        {
            Assert.False(string.IsNullOrWhiteSpace(ParagraphItemDisplay.GetPauseLabel(null)));
        }

        [Theory]
        [InlineData(ParagraphItemType.Narration)]
        [InlineData(ParagraphItemType.VolumePause)]
        [InlineData(ParagraphItemType.ChapterPause)]
        public void GetItemDisplay_ReturnsNonEmptyIconAndLabel(ParagraphItemType type)
        {
            var (icon, _, label) = ParagraphItemDisplay.GetItemDisplay(type);
            Assert.False(string.IsNullOrWhiteSpace(icon));
            Assert.False(string.IsNullOrWhiteSpace(label));
        }
    }
}
