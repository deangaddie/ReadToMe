using Read2Me.App.State;
using Read2Me.Data;
using Read2Me.Data.Entities;
using Read2Me.Data.Enums;
using Xunit;

namespace Read2Me.Tests.App
{
    public class ParagraphCharacterStampTests
    {
        private static ParagraphItem Item(ParagraphItemType type, Guid? charId = null) =>
            new() { Id = Guid.NewGuid(), ParagraphId = Guid.NewGuid(), Order = "a", ItemType = type, CharacterId = charId };

        // Narration is the narrator as speaker, not a type (ADR-0006).
        private static ParagraphItem Narration() =>
            Item(ParagraphItemType.Speech, ProjectDbContext.NarratorId);

        private static Character Char(Guid id, string name = "Alice") =>
            new() { Id = id, Name = name };

        [Fact]
        public void Apply_SetsCharacterOnAllCharacterItems()
        {
            var charId = Guid.NewGuid();
            var character = Char(charId);
            var items = new List<ParagraphItem>
            {
                Item(ParagraphItemType.Speech),
                Item(ParagraphItemType.Speech),
                Narration(),
            };

            var changed = ParagraphCharacterStamp.Apply(items, charId, character);

            Assert.True(changed);
            Assert.Equal(charId, items[0].CharacterId);
            Assert.Equal(charId, items[1].CharacterId);
            Assert.Same(character, items[0].Character);
            Assert.Same(character, items[1].Character);
            Assert.Equal(ProjectDbContext.NarratorId, items[2].CharacterId);
        }

        [Fact]
        public void Apply_ToNarrator_MakesTheParagraphNarrationAndIsIdempotent()
        {
            var existingId = Guid.NewGuid();
            var items = new List<ParagraphItem>
            {
                Item(ParagraphItemType.Speech, existingId),
                Item(ParagraphItemType.Speech),   // unattributed dialog
                Narration(),
            };

            Assert.True(ParagraphCharacterStamp.Apply(items, ProjectDbContext.NarratorId, null));
            Assert.All(items, i => Assert.Equal(ProjectDbContext.NarratorId, i.CharacterId));

            Assert.False(ParagraphCharacterStamp.Apply(items, ProjectDbContext.NarratorId, null));
        }

        [Fact]
        public void Apply_AllNarrationParagraph_TakesTheNarration_OnlyWhenAsked()
        {
            var charId = Guid.NewGuid();
            List<ParagraphItem> Narrated() =>
            [
                Narration(),
                Item(ParagraphItemType.Pause),
            ];

            // The bulk fan-out's rule: narration survives, whatever the paragraph looks like.
            var bulk = Narrated();
            Assert.False(ParagraphCharacterStamp.Apply(bulk, charId, Char(charId)));
            Assert.Equal(ProjectDbContext.NarratorId, bulk[0].CharacterId);

            // The single-paragraph gesture's rule: with no dialog left, the narration is the
            // paragraph, so it moves — and the pause still does not.
            var single = Narrated();
            Assert.True(ParagraphCharacterStamp.Apply(
                single, charId, Char(charId), sweepAllNarrationParagraph: true));
            Assert.Equal(charId, single[0].CharacterId);
            Assert.Null(single[1].CharacterId);
        }

        [Fact]
        public void Apply_MixedParagraph_LeavesNarrationAlone_EvenWhenSweepIsAsked()
        {
            var charId = Guid.NewGuid();
            var items = new List<ParagraphItem>
            {
                Narration(),
                Item(ParagraphItemType.Speech),   // unattributed dialog
            };

            Assert.True(ParagraphCharacterStamp.Apply(
                items, charId, Char(charId), sweepAllNarrationParagraph: true));

            Assert.Equal(ProjectDbContext.NarratorId, items[0].CharacterId);
            Assert.Equal(charId, items[1].CharacterId);
        }

        [Fact]
        public void Apply_IsIdempotent()
        {
            var charId = Guid.NewGuid();
            var character = Char(charId);
            var items = new List<ParagraphItem>
            {
                Item(ParagraphItemType.Speech, charId),
            };
            items[0].Character = character;

            var changed = ParagraphCharacterStamp.Apply(items, charId, character);

            Assert.False(changed);
            Assert.Equal(charId, items[0].CharacterId);
        }

        [Fact]
        public void Apply_WithNullCharacterId_ClearsAttribution()
        {
            var existingId = Guid.NewGuid();
            var items = new List<ParagraphItem>
            {
                Item(ParagraphItemType.Speech, existingId),
                Item(ParagraphItemType.Speech, existingId),
            };

            var changed = ParagraphCharacterStamp.Apply(items, null, null);

            Assert.True(changed);
            Assert.Null(items[0].CharacterId);
            Assert.Null(items[0].Character);
            Assert.Null(items[1].CharacterId);
            Assert.Null(items[1].Character);
        }

        [Fact]
        public void Apply_IgnoresPauseItems()
        {
            var charId = Guid.NewGuid();
            var items = new List<ParagraphItem>
            {
                Item(ParagraphItemType.Pause),
                Item(ParagraphItemType.VolumePause),
                Item(ParagraphItemType.PartPause),
                Item(ParagraphItemType.ChapterPause),
                Item(ParagraphItemType.ParagraphPause),
            };

            ParagraphCharacterStamp.Apply(items, charId, Char(charId));

            foreach (var item in items)
                Assert.Null(item.CharacterId);
        }

        [Fact]
        public void PlaceholderFor_CarriesIdAndName()
        {
            var id = Guid.NewGuid();
            var placeholder = ParagraphCharacterStamp.PlaceholderFor(id, "Bob");

            Assert.Equal(id, placeholder.Id);
            Assert.Equal("Bob", placeholder.Name);
        }
    }
}
