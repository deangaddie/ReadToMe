using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
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
