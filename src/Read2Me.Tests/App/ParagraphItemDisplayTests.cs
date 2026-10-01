using Read2Me.App.Shared;
using MudBlazor;
using Read2Me.Data;
using Read2Me.Data.Entities;
using Read2Me.Data.Enums;
using Xunit;

namespace Read2Me.Tests.App
{
    public class ParagraphItemDisplayTests
    {
        [Fact]
        public void GetSpeechDisplay_NarratorStampedItem_ShowsTheNarrationPresentation()
        {
            var item = new ParagraphItem
            {
                Id = Guid.NewGuid(), Order = "a",
                ItemType = ParagraphItemType.Speech,   // the type says nothing any more
                CharacterId = ProjectDbContext.NarratorId,
            };

            var (icon, color, label) = ParagraphItemDisplay.GetSpeechDisplay(item);

            Assert.Equal("Narration", label);
            Assert.Equal(Color.Info, color);
            Assert.False(string.IsNullOrEmpty(icon));
        }

        [Fact]
        public void GetSpeechDisplay_CharacterStampedItem_ShowsThatCharactersChip()
        {
            var alice = new Character { Id = Guid.NewGuid(), Name = "Alice" };
            var item = new ParagraphItem
            {
                Id = Guid.NewGuid(), Order = "a",
                ItemType = ParagraphItemType.Speech,   // the type says nothing any more
                CharacterId = alice.Id,
                Character = alice,
            };

            var (icon, color, label) = ParagraphItemDisplay.GetSpeechDisplay(item);

            Assert.Equal("Alice", label);
            Assert.Equal(Color.Primary, color);
            Assert.Equal("", icon);
        }

        [Fact]
        public void GetSpeechDisplay_UnattributedItem_StaysVisiblyDistinct()
        {
            var item = new ParagraphItem
            {
                Id = Guid.NewGuid(), Order = "a",
                ItemType = ParagraphItemType.Speech,
                CharacterId = null,
            };

            var (_, color, label) = ParagraphItemDisplay.GetSpeechDisplay(item);

            Assert.Equal("Unknown", label);
            Assert.Equal(Color.Warning, color);
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
        [InlineData(ParagraphItemType.VolumePause)]
        [InlineData(ParagraphItemType.ChapterPause)]
        public void GetPauseDisplay_ReturnsNonEmptyIconAndLabel(ParagraphItemType type)
        {
            var (icon, _, label) = ParagraphItemDisplay.GetPauseDisplay(type);
            Assert.False(string.IsNullOrWhiteSpace(icon));
            Assert.False(string.IsNullOrWhiteSpace(label));
        }
    }
}
