using Read2Me.Data;
using Read2Me.Data.Entities;
using Read2Me.Services.Characters;
using Xunit;

namespace Read2Me.Tests.Services.Characters
{
    /// <summary>
    /// The narrator token is judged before the roster walk. That ordering is the whole point: the
    /// roster always contains the seed <c>Narrator</c> row, so a roster-first walk would make the
    /// token "known" whether or not a link is set, and the unlinked case could never fire.
    /// </summary>
    public class CharacterNamesTests
    {
        private static readonly IReadOnlyList<Character> Roster =
        [
            // The seed row, always present — and exactly what would silently answer "known".
            new Character { Name = "Narrator", IsNarrator = true },
            new Character { Name = "Dr. Watson", Aliases = [new CharacterAlias { Name = "Watson" }] },
        ];

        private static readonly NarratorIdentity Linked = new(Guid.NewGuid(), "Dr. Watson", true);

        [Fact]
        public void NarratorToken_Linked_CanonicalizesToTheLinkedCharacter()
        {
            Assert.Equal("Dr. Watson", CharacterNames.Canonicalize("narrator", Roster, Linked));
            Assert.True(CharacterNames.IsKnown("Narrator ", Roster, Linked));
        }

        /// <summary>
        /// Unlinked the token owns nobody — not even the seed row it would otherwise match by name.
        /// It canonicalizes to itself rather than to null: null is what a blank speaker yields, and
        /// two samples answering "narrator" and "" must not compare equal.
        /// </summary>
        [Fact]
        public void NarratorToken_Unlinked_OwnsNobody()
        {
            Assert.False(CharacterNames.IsKnown("narrator", Roster, NarratorIdentity.Unlinked));
            Assert.Equal("narrator", CharacterNames.Canonicalize(" narrator ", Roster, NarratorIdentity.Unlinked));
        }

        [Fact]
        public void OrdinaryNamesAndAliases_AreUnaffectedByTheLink()
        {
            foreach (var narrator in new[] { NarratorIdentity.Unlinked, Linked })
            {
                Assert.Equal("Dr. Watson", CharacterNames.Canonicalize(" Watson ", Roster, narrator));
                Assert.True(CharacterNames.IsKnown("dr. watson", Roster, narrator));
                Assert.Equal("Mock Turtle", CharacterNames.Canonicalize("Mock Turtle", Roster, narrator));
                Assert.False(CharacterNames.IsKnown("Mock Turtle", Roster, narrator));
                Assert.Null(CharacterNames.Canonicalize(null, Roster, narrator));
            }
        }
    }
}
