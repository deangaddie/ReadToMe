using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Read2Me.Data;
using Read2Me.Data.Entities;

namespace Read2Me.TestUtils
{
    /// <summary>
    /// The project schema before the latest migration, for tests that play an older build's files
    /// (ADR 0016, "Snapshots"). History's chunks only fit both schemas while the latest migration
    /// leaves every state table alone, so <see cref="MigrationId"/> fails loudly once one doesn't.
    /// </summary>
    public static class PriorSchema
    {
        /// <summary>A migration's id and what its <c>Up</c> does.</summary>
        public sealed record MigrationStep(string Id, IReadOnlyList<MigrationOperation> Up);

        private static readonly Lazy<IReadOnlyList<MigrationStep>> ProjectMigrations = new(ReadProjectMigrations);

        /// <summary>The project's tables that hold no Project state; matches <c>ProjectHistory.NotStateTables</c>.</summary>
        public static IReadOnlyList<string> NotStateTables { get; } = ReadNotStateTables();

        private static readonly Lazy<string> Prior = new(() => PriorOf(ProjectMigrations.Value, NotStateTables));

        /// <summary>The id of the migration before the project's latest.</summary>
        public static string MigrationId => Prior.Value;

        /// <summary>
        /// The id of the migration before the last of <paramref name="migrations"/>; throws when the
        /// last touches a table outside <paramref name="notStateTables"/>, or runs SQL nobody can read.
        /// </summary>
        public static string PriorOf(IReadOnlyList<MigrationStep> migrations, IReadOnlyCollection<string> notStateTables)
        {
            if (migrations.Count < 2)
                throw new InvalidOperationException("The project has no migration before its latest to go back to.");
            var latest = migrations[^1];
            var state = latest.Up.SelectMany(TablesOf).Where(t => t is null || !notStateTables.Contains(t)).ToList();
            if (state.Count > 0)
                throw new InvalidOperationException(
                    $"The latest migration {latest.Id} touches "
                    + string.Join(", ", state.Select(t => t ?? "tables its SQL names"))
                    + $", which hold Project state: History's chunks recorded on the schema before it no longer fit it, "
                    + $"so {nameof(PriorSchema)} can't play an older build. Rework {nameof(PriorSchema)} and the tests that use it: "
                    + "for example, record the files on the prior schema "
                    + "rather than migrating them down afterwards.");
            return migrations[^2].Id;
        }

        /// <summary>Takes <paramref name="databaseFile"/> back to <see cref="MigrationId"/>.</summary>
        public static async Task MigrateAsync(string databaseFile)
        {
            await using var db = Open(databaseFile);
            await db.GetService<IMigrator>().MigrateAsync(MigrationId);
        }

        /// <summary>
        /// Takes <paramref name="databaseFile"/> back to <see cref="MigrationId"/> with a table the latest
        /// migration creates already there, so migrating it, or any step opened from it, fails.
        /// </summary>
        public static async Task BreakAsync(string databaseFile)
        {
            var created = ProjectMigrations.Value[^1].Up.OfType<CreateTableOperation>().FirstOrDefault()
                ?? throw new InvalidOperationException(
                    $"The latest migration {ProjectMigrations.Value[^1].Id} creates no table, so {nameof(BreakAsync)} "
                    + "can't make it fail by creating one early. Give it another way to fail.");
            await MigrateAsync(databaseFile);
            await using var connection = new SqliteConnection($"Data Source={databaseFile};Pooling=false");
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"CREATE TABLE \"{created.Name}\" (Broken INTEGER)";
            await command.ExecuteNonQueryAsync();
        }

        /// <summary>The tables an operation touches; null for one whose tables can't be told, like raw SQL.</summary>
        private static IEnumerable<string?> TablesOf(MigrationOperation operation) => operation switch
        {
            CreateTableOperation create => [create.Name],
            DropTableOperation drop => [drop.Name],
            RenameTableOperation rename => [rename.Name, rename.NewName],
            ITableMigrationOperation table => [table.Table],
            _ => [null],
        };

        private static IReadOnlyList<MigrationStep> ReadProjectMigrations()
        {
            using var db = Open(":memory:");
            var assembly = db.GetService<IMigrationsAssembly>();
            return assembly.Migrations.OrderBy(m => m.Key, StringComparer.Ordinal)
                .Select(m => new MigrationStep(m.Key, assembly.CreateMigration(m.Value, db.Database.ProviderName!).UpOperations))
                .ToList();
        }

        private static IReadOnlyList<string> ReadNotStateTables()
        {
            using var db = Open(":memory:");
            return
            [
                db.Model.FindEntityType(typeof(AudioReview))!.GetTableName()!,
                db.Model.FindEntityType(typeof(StepCheckpoint))!.GetTableName()!,
            ];
        }

        private static ProjectDbContext Open(string databaseFile) =>
            new(new DbContextOptionsBuilder<ProjectDbContext>().UseSqlite($"Data Source={databaseFile};Pooling=false").Options);
    }
}
