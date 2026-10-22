using Microsoft.Data.Sqlite;

namespace Read2Me.TestUtils
{
    /// <summary>A project's History damaged as a bad disk or a stray edit would leave it, for tests of steps that can't be opened.</summary>
    public static class DamagedHistory
    {
        /// <summary>Cuts every recorded chunk in <paramref name="historyDatabase"/> in half, so replaying any of them fails.</summary>
        public static async Task DamageChunksAsync(string historyDatabase)
        {
            await using var connection = new SqliteConnection($"Data Source={historyDatabase};Pooling=false");
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE Chunk SET Patchset = substr(Patchset, 1, length(Patchset) / 2) WHERE length(Patchset) > 0";
            if (await command.ExecuteNonQueryAsync() == 0)
                throw new InvalidOperationException($"{historyDatabase} has no chunk to damage.");
        }
    }
}
