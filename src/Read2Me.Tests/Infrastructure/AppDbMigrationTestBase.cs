using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Read2Me.AppData;

namespace Read2Me.Tests.Infrastructure
{
    /// <summary>
    /// File-based SQLite test base that runs the full Read2MeDbContext migration chain,
    /// allowing schema assertions after MigrateAsync.
    /// </summary>
    public abstract class AppDbMigrationTestBase : IAsyncDisposable
    {
        private readonly string _tempDir;
        private readonly string _dbPath;

        protected AppDbMigrationTestBase()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "Read2MeAppDbTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
            _dbPath = Path.Combine(_tempDir, "app.db");
        }

        protected async Task<Read2MeDbContext> OpenDbAsync()
        {
            var db = NewContext();
            await db.Database.MigrateAsync();
            return db;
        }

        /// <summary>
        /// Migrates the same file only as far as <paramref name="migration"/>, so a test can seed the
        /// rows a later data migration is supposed to rewrite. Call <see cref="OpenDbAsync"/> after
        /// to finish the chain against that seeded state.
        /// </summary>
        protected async Task<Read2MeDbContext> OpenDbAtAsync(string migration)
        {
            var db = NewContext();
            await db.GetInfrastructure().GetRequiredService<IMigrator>().MigrateAsync(migration);
            return db;
        }

        private Read2MeDbContext NewContext() =>
            new(new DbContextOptionsBuilder<Read2MeDbContext>()
                .UseSqlite($"Data Source={_dbPath};Pooling=false")
                .Options);

        public ValueTask DisposeAsync()
        {
            try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); }
            catch { /* best effort */ }
            GC.SuppressFinalize(this);
            return ValueTask.CompletedTask;
        }
    }
}
