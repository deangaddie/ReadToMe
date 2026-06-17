using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Read2Me.Data;
using Read2Me.Tests.Infrastructure;
using Xunit;

namespace Read2Me.Tests.Services
{
    /// <summary>
    /// Verifies that the full migration chain produces the expected schema, including
    /// tables that were in the model snapshot but previously missing from migrations (Voices, VoiceInstructions).
    /// </summary>
    public class MigrationRoundTripTests : ProjectDbTestBase
    {
        [Fact]
        public async Task MigrateAsync_CreatesVoicesTable()
        {
            await using var db = await OpenDbAsync();
            var conn = (SqliteConnection)db.Database.GetDbConnection();
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='Voices'";
            var result = await cmd.ExecuteScalarAsync();
            Assert.Equal("Voices", result?.ToString());
        }

        [Fact]
        public async Task MigrateAsync_CreatesCharacterAliasesTable()
        {
            await using var db = await OpenDbAsync();
            var conn = (SqliteConnection)db.Database.GetDbConnection();
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='CharacterAliases'";
            var result = await cmd.ExecuteScalarAsync();
            Assert.Equal("CharacterAliases", result?.ToString());
        }

        [Fact]
        public async Task MigrateAsync_AddsParagraphItemVoiceInstructionsColumn()
        {
            await using var db = await OpenDbAsync();
            var conn = (SqliteConnection)db.Database.GetDbConnection();
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA table_info(ParagraphItems)";
            await using var reader = await cmd.ExecuteReaderAsync();
            bool found = false;
            while (await reader.ReadAsync())
            {
                if (reader.GetString(1) == "VoiceInstructions") { found = true; break; }
            }
            Assert.True(found, "VoiceInstructions column should exist on ParagraphItems");
        }
    }
}
