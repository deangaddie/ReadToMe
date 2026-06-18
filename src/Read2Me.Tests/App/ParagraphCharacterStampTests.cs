using System;
using System.Collections.Generic;
using Read2Me.App.State;
using Read2Me.Data.Entities;
using Read2Me.Data.Enums;
using Xunit;

namespace Read2Me.Tests.App
{
    public class ParagraphCharacterStampTests
    {
        private static ParagraphItem Item(ParagraphItemType type, Guid? charId = null) =>
            new() { Id = Guid.NewGuid(), ParagraphId = Guid.NewGuid(), Order = "a", ItemType = type, CharacterId = charId };

        private static Character Char(Guid id, string name = "Alice") =>
            new() { Id = id, Name = name };

        [Fact]
        public void Apply_SetsCharacterOnAllCharacterItems()
        {
            var charId = Guid.NewGuid();
            var character = Char(charId);
            var items = new List<ParagraphItem>
            {
                Item(ParagraphItemType.Character),
                Item(ParagraphItemType.Character),
                Item(ParagraphItemType.Narration),
            };

            var changed = ParagraphCharacterStamp.Apply(items, charId, character);

            Assert.True(changed);
            Assert.Equal(charId, items[0].CharacterId);
            Assert.Equal(charId, items[1].CharacterId);
            Assert.Same(character, items[0].Character);
            Assert.Same(character, items[1].Character);
            Assert.Null(items[2].CharacterId);
        }

        [Fact]
        public void Apply_IsIdempotent()
        {
            var charId = Guid.NewGuid();
            var character = Char(charId);
            var items = new List<ParagraphItem>
            {
                Item(ParagraphItemType.Character, charId),
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
                Item(ParagraphItemType.Character, existingId),
                Item(ParagraphItemType.Character, existingId),
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
