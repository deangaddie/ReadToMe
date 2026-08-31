using Microsoft.EntityFrameworkCore;
using Read2Me.Data;
using Read2Me.Data.Entities;
using Read2Me.Data.Enums;
using Read2Me.Tests.Infrastructure;
using Xunit;

namespace Read2Me.Tests.Narrator
{
    /// <summary>
    /// NarrationRule is the one place that answers "is this narration?", from the speaker
    /// alone. Nothing consumes it yet — these tests are the proof that the later consumer
    /// batches can switch off ItemType without changing behaviour.
    /// </summary>
    public class NarrationRuleTests : ProjectDbTestBase
    {
        [Fact]
        public void IsNarration_NarratorStampedItem_IsNarration()
        {
            var item = new ParagraphItem { CharacterId = ProjectDbContext.NarratorId };

            Assert.True(NarrationRule.IsNarration(item));
        }

        [Fact]
        public void IsNarration_CharacterStampedItem_IsNot()
        {
            var item = new ParagraphItem { CharacterId = Guid.NewGuid() };

            Assert.False(NarrationRule.IsNarration(item));
        }

        [Fact]
        public void IsNarration_UnattributedItem_IsNot()
        {
            var item = new ParagraphItem { CharacterId = null };

            Assert.False(NarrationRule.IsNarration(item));
        }

        [Fact]
        public void IsNarration_PauseItem_IsNot()
        {
            var item = new ParagraphItem { ItemType = ParagraphItemType.ChapterPause, CharacterId = null };

            Assert.False(NarrationRule.IsNarration(item));
        }

        /// <summary>
        /// The equivalence that makes the later batches safe: over a book carrying every kind
        /// of item, the speaker-only rule agrees with today's ItemType on every row.
        /// </summary>
        [Fact]
        public async Task IsNarration_AgreesWithItemType_OnEveryItemOfASeededBook()
        {
            await SeedMixedBookAsync();

            await using var db = await OpenDbAsync();
            var items = await db.ParagraphItems.AsNoTracking().ToListAsync();

            Assert.Equal(6, items.Count);
            foreach (var item in items)
            {
                Assert.Equal(item.ItemType == ParagraphItemType.Narration, NarrationRule.IsNarration(item));
            }
        }

        [Fact]
        public async Task IsNarrationExpression_SelectsTheSameItemsAsThePredicate()
        {
            await SeedMixedBookAsync();

            await using var db = await OpenDbAsync();
            var fromDb = await db.ParagraphItems.AsNoTracking()
                .Where(NarrationRule.IsNarrationExpression)
                .Select(i => i.Id)
                .ToListAsync();
            var inMemory = (await db.ParagraphItems.AsNoTracking().ToListAsync())
                .Where(NarrationRule.IsNarration)
                .Select(i => i.Id);

            Assert.Equal(inMemory.OrderBy(id => id), fromDb.OrderBy(id => id));
        }

        /// <summary>
        /// The readers ask this inside LINQ that must reach SQL — if the comparison fell back
        /// to client evaluation the whole items table would be pulled per query.
        /// </summary>
        [Fact]
        public async Task IsNarrationExpression_TranslatesToSql()
        {
            await using var db = await OpenDbAsync();

            var sql = db.ParagraphItems.Where(NarrationRule.IsNarrationExpression).ToQueryString();

            Assert.Contains("WHERE", sql, StringComparison.Ordinal);
            var where = sql[(sql.IndexOf("WHERE", StringComparison.Ordinal) + 5)..];
            Assert.Contains("\"CharacterId\"", where, StringComparison.Ordinal);
            Assert.Contains(ProjectDbContext.NarratorId.ToString(), where, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Narration, attributed dialog, unattributed dialog and a pause.</summary>
        private async Task SeedMixedBookAsync()
        {
            var holmes = new Character { Id = Guid.NewGuid(), Name = "Holmes" };
            var b = new BookHierarchyBuilder(OpenDbAsync).WithCharacter("Holmes", holmes);
            await b.AddVolume("V1", v => v.AddChapter("C1", c =>
            {
                c.AddParagraph("P1", p => p
                    .AddNarration("n1")
                    .AddCharacterLine("d1", "\"Elementary.\"", "Holmes")
                    .AddRawItem("u1", ParagraphItemType.Character, "\"Who said that?\"", characterId: null)
                    .AddPause("pz1", ParagraphItemType.ParagraphPause));
                c.AddParagraph("P2", p => p
                    .AddNarration("n2")
                    .AddRawItem("u2", ParagraphItemType.Character, "\"And that?\"", characterId: null));
            })).BuildAsync();
        }
    }
}
