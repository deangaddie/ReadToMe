using Read2Me.Services.Characters;
using Xunit;

namespace Read2Me.Tests.Services.Characters
{
    public class CharacterDiscoveryParserTests
    {
        [Fact]
        public void EmptyCharacterArray_ParsesToEmptyList()
        {
            var ok = CharacterDiscoveryParser.TryParse(
                """{ "reasoning": "none found", "characters": [] }""",
                out var characters, out var error);

            Assert.True(ok);
            Assert.Empty(characters);
            Assert.Null(error);
        }

        [Fact]
        public void CharacterWithNoAliases_ParsesWithEmptyAliasList()
        {
            var ok = CharacterDiscoveryParser.TryParse(
                """{ "reasoning": "r", "characters": [ { "name": "Bilbo", "aliases": [] } ] }""",
                out var characters, out _);

            Assert.True(ok);
            var c = Assert.Single(characters);
            Assert.Equal("Bilbo", c.Name);
            Assert.Empty(c.Aliases);
        }

        [Fact]
        public void MissingAliasesField_TreatedAsEmpty()
        {
            var ok = CharacterDiscoveryParser.TryParse(
                """{ "reasoning": "r", "characters": [ { "name": "Gandalf" } ] }""",
                out var characters, out _);

            Assert.True(ok);
            Assert.Equal("Gandalf", Assert.Single(characters).Name);
            Assert.Empty(characters[0].Aliases);
        }

        [Fact]
        public void EntryMissingName_IsDropped()
        {
            var ok = CharacterDiscoveryParser.TryParse(
                """
                { "reasoning": "r", "characters": [
                  { "aliases": ["x"] },
                  { "name": "Thorin", "aliases": ["Oakenshield"] }
                ] }
                """,
                out var characters, out _);

            Assert.True(ok);
            var c = Assert.Single(characters);
            Assert.Equal("Thorin", c.Name);
            Assert.Equal(["Oakenshield"], c.Aliases);
        }

        [Fact]
        public void BlankAliases_AreDiscarded()
        {
            var ok = CharacterDiscoveryParser.TryParse(
                """{ "reasoning": "r", "characters": [ { "name": "Bilbo", "aliases": ["Mr. Baggins", "", "  "] } ] }""",
                out var characters, out _);

            Assert.True(ok);
            Assert.Equal(["Mr. Baggins"], Assert.Single(characters).Aliases);
        }

        [Fact]
        public void CodeFencedResponse_IsParsed()
        {
            var ok = CharacterDiscoveryParser.TryParse(
                "```json\n{ \"reasoning\": \"r\", \"characters\": [ { \"name\": \"Bilbo\", \"aliases\": [] } ] }\n```",
                out var characters, out _);

            Assert.True(ok);
            Assert.Equal("Bilbo", Assert.Single(characters).Name);
        }

        [Fact]
        public void Junk_ReturnsFalse()
        {
            var ok = CharacterDiscoveryParser.TryParse("not json at all", out var characters, out var error);

            Assert.False(ok);
            Assert.Empty(characters);
            Assert.NotNull(error);
        }

        [Fact]
        public void ObjectWithoutCharactersArray_ReturnsFalse()
        {
            var ok = CharacterDiscoveryParser.TryParse("""{ "reasoning": "r" }""", out _, out var error);

            Assert.False(ok);
            Assert.NotNull(error);
        }

        [Fact]
        public void EmptyString_ReturnsFalse()
        {
            var ok = CharacterDiscoveryParser.TryParse("   ", out _, out var error);

            Assert.False(ok);
            Assert.NotNull(error);
        }
    }
}
