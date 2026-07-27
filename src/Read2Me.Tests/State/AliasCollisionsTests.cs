using Read2Me.App.State;
using Read2Me.Data.Entities;
using Xunit;

namespace Read2Me.Tests.State
{
    public class AliasCollisionsTests
    {
        private static DiscoveredCharacterRow Row(string name, params string[] aliases) =>
            new() { Name = name, Aliases = [.. aliases] };

        private static Character Existing(string name, params string[] aliases) =>
            new()
            {
                Id = Guid.NewGuid(),
                Name = name,
                Aliases = [.. aliases.Select(a => new CharacterAlias { Name = a })],
            };

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
            var row = Row("Elizabeth Bennet", "Lizzy");
            row.AlreadyExists = true;
            row.ExistingCharacterId = elizabeth.Id;

            Assert.Empty(AliasCollisions.Find([row], [elizabeth]));
        }

        [Fact]
        public void ExcludedRows_DoNotCollide()
        {
            var jane = Row("Jane Bennet", "Miss Bennet");
            jane.Included = false;

            var found = AliasCollisions.Find(
                [Row("Elizabeth Bennet", "Miss Bennet"), jane], []);

            Assert.Empty(found);
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
