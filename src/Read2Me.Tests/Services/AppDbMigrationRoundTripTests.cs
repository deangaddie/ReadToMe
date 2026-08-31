using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Read2Me.AppData.Migrations;
using Read2Me.Tests.Infrastructure;
using Xunit;

namespace Read2Me.Tests.Services
{
    public class AppDbMigrationRoundTripTests : AppDbMigrationTestBase
    {
        [Fact]
        public async Task MigrateAsync_CreatesSemanticSimilarityServiceConfigsTable()
        {
            await using var db = await OpenDbAsync();
            var conn = (SqliteConnection)db.Database.GetDbConnection();
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='SemanticSimilarityServiceConfigs'";
            var result = await cmd.ExecuteScalarAsync();
            Assert.Equal("SemanticSimilarityServiceConfigs", result?.ToString());
        }

        [Fact]
        public async Task MigrateAsync_AddsActiveSemanticConfigIdToAppSettings()
        {
            await using var db = await OpenDbAsync();
            var conn = (SqliteConnection)db.Database.GetDbConnection();
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA table_info(Settings)";
            await using var reader = await cmd.ExecuteReaderAsync();
            bool found = false;
            while (await reader.ReadAsync())
            {
                if (reader.GetString(1) == "ActiveSemanticConfigId") { found = true; break; }
            }
            Assert.True(found, "ActiveSemanticConfigId column should exist on Settings");
        }

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

        [Fact]
        public async Task MigrateAsync_AddsAudioMaxAttemptsToSettings()
        {
            await using var db = await OpenDbAsync();
            var conn = (SqliteConnection)db.Database.GetDbConnection();
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA table_info(Settings)";
            await using var reader = await cmd.ExecuteReaderAsync();
            bool found = false;
            while (await reader.ReadAsync())
            {
                if (reader.GetString(1) == "AudioMaxAttempts") { found = true; break; }
            }
            Assert.True(found, "AudioMaxAttempts column should exist on Settings");
        }
    }
}
