using Read2Me.Core.Models;
using Xunit;

namespace Read2Me.Tests.Core
{
    /// <summary>
    /// Fixtures mirrored by <c>alias-collisions.spec.ts</c> in the web client; keep the two in step.
    /// </summary>
    public class AliasCollisionsTests
    {
        private static AliasClaim Row(string name, params string[] aliases) => new(name, aliases);

        private static AliasOwner Existing(string name, params string[] aliases) =>
            new(Guid.NewGuid(), name, aliases);

        [Fact]
        public void NoSharedNames_NoCollisions()
        {
            var found = AliasCollisions.Find(
                [Row("Elizabeth Bennet", "Lizzy"), Row("Jane Bennet", "Jane")], []);

            Assert.Empty(found);
        }

        [Fact]
        public void SameAliasOnTwoRows_IsACollision()
        {
            // The observed Pride and Prejudice case: discovery hands "Miss Bennet" to every sister.
            var found = AliasCollisions.Find(
                [
                    Row("Elizabeth Bennet", "Lizzy", "Miss Bennet"),
                    Row("Jane Bennet", "Miss Bennet"),
                    Row("Mary Bennet", "Miss Bennet"),
                ], []);

            Assert.Equal(["Miss Bennet"], found);
        }

        [Fact]
        public void RowNameMatchingAnotherRowsAlias_IsACollision()
        {
            var found = AliasCollisions.Find(
                [Row("Miss Bennet"), Row("Elizabeth Bennet", "Miss Bennet")], []);

            Assert.Equal(["Miss Bennet"], found);
        }

        [Fact]
        public void CollisionWithTheExistingRoster_IsFound()
        {
            var found = AliasCollisions.Find(
                [Row("Jane Bennet", "Miss Bennet")],
                [Existing("Elizabeth Bennet", "Miss Bennet")]);

            Assert.Equal(["Miss Bennet"], found);
        }

        [Fact]
        public void TheRosterCharacterARowResolvesOnto_IsNotASecondOwner()
        {
            // Re-running discovery re-proposes characters that already exist. A row merging into
            // Elizabeth is Elizabeth — counting the roster row too would flag every alias it keeps.
            var elizabeth = Existing("Elizabeth Bennet", "Lizzy");
            var row = Row("Elizabeth Bennet", "Lizzy") with { ExistingCharacterId = elizabeth.Id };

            Assert.Empty(AliasCollisions.Find([row], [elizabeth]));
        }

        [Fact]
        public void MatchingIsCaseAndWhitespaceInsensitive()
        {
            // CharacterResolver.Matches is OrdinalIgnoreCase, so casing does not save a shared alias.
            var found = AliasCollisions.Find(
                [Row("Elizabeth Bennet", " miss bennet "), Row("Jane Bennet", "Miss Bennet")], []);

            Assert.Equal(["miss bennet"], found, StringComparer.OrdinalIgnoreCase);
        }

        [Fact]
        public void OneRowRepeatingItsOwnAlias_IsNotACollision()
        {
            // Untidy, not ambiguous: it still resolves to one character.
            Assert.Empty(AliasCollisions.Find([Row("Elizabeth Bennet", "Lizzy", "lizzy")], []));
        }

        [Fact]
        public void BlankNamesAreIgnored()
        {
            Assert.Empty(AliasCollisions.Find(
                [Row("Elizabeth Bennet", "  "), Row("Jane Bennet", "")], []));
        }
    }
}
