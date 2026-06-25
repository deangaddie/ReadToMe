using Microsoft.EntityFrameworkCore;
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
            var options = new DbContextOptionsBuilder<Read2MeDbContext>()
                .UseSqlite($"Data Source={_dbPath};Pooling=false")
                .Options;
            var db = new Read2MeDbContext(options);
            await db.Database.MigrateAsync();
            return db;
        }

        public ValueTask DisposeAsync()
        {
            try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); }
            catch { /* best effort */ }
            GC.SuppressFinalize(this);
            return ValueTask.CompletedTask;
        }
    }
}
