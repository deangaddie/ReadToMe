using Microsoft.EntityFrameworkCore;
using Read2Me.AppData.Migrations;
using Read2Me.Tests.Infrastructure;
using Xunit;

namespace Read2Me.Tests.Services
{
    public class AppDbMigrationRoundTripTests : AppDbMigrationTestBase
    {
        /// <summary>
        /// Spec §5: a prompt stored against the retired segment ask fail-parses every paragraph, so
        /// the frozen-boundary migration clears all four unconditionally — hand-edits included. The
        /// seeded row is what an install that customised its prompts looks like on disk.
        /// </summary>
        [Fact]
        public async Task MigrateAsync_ClearsHandEditedAttributionPrompts_KeepsOtherPrompts()
        {
            // nameof, not the timestamped id: a migration inserted before this one would silently
            // move the seed point, and the compiler cannot see a string drift.
            await using (var before = await OpenDbAtAsync(nameof(AddSupportsModelSwitch)))
            {
                before.PromptSettings.Add(new()
                {
                    CharacterPrompt = "hand-edited: split the paragraph into segments",
                    BatchCharacterPrompt = "hand-edited batch",
                    SimpleCharacterPrompt = "hand-edited simple",
                    SimpleBatchCharacterPrompt = "hand-edited simple batch",
                    VoicePrompt = "hand-edited voice",
                    DiscoverCharactersPrompt = "hand-edited discovery",
                    ContextParagraphsBefore = 3,
                });
                await before.SaveChangesAsync();
            }

            await using var db = await OpenDbAsync();
            var row = await db.PromptSettings.SingleAsync();

            Assert.Null(row.CharacterPrompt);
            Assert.Null(row.BatchCharacterPrompt);
            Assert.Null(row.SimpleCharacterPrompt);
            Assert.Null(row.SimpleBatchCharacterPrompt);
            // Only the attribution templates changed shape — everything else stays the user's.
            Assert.Equal("hand-edited voice", row.VoicePrompt);
            Assert.Equal("hand-edited discovery", row.DiscoverCharactersPrompt);
            Assert.Equal(3, row.ContextParagraphsBefore);
        }
    }
}
