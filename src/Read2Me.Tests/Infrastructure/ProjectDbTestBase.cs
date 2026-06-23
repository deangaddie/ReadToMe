using Microsoft.EntityFrameworkCore;
using Read2Me.Data;

namespace Read2Me.Tests.Infrastructure
{
    public abstract class ProjectDbTestBase : IAsyncDisposable
    {
        protected string TempDir { get; }
        protected string FolderName { get; } = "test-book";

        protected ProjectDbTestBase()
        {
            TempDir = Path.Combine(Path.GetTempPath(), "Read2MeTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(TempDir);
        }

        protected string FolderPath
        {
            get
            {
                var path = Path.Combine(TempDir, FolderName);
                Directory.CreateDirectory(path);
                return path;
            }
        }

        /// <summary>Opens a new migrated ProjectDbContext each call. Caller owns dispose.</summary>
        protected async Task<ProjectDbContext> OpenDbAsync()
        {
            var options = new DbContextOptionsBuilder<ProjectDbContext>()
                .UseSqlite($"Data Source={Path.Combine(FolderPath, "project.db")};Pooling=false")
                .Options;
            var db = new ProjectDbContext(options);
            await db.Database.MigrateAsync();
            return db;
        }

        public ValueTask DisposeAsync()
        {
            try { if (Directory.Exists(TempDir)) Directory.Delete(TempDir, recursive: true); }
            catch { /* best effort */ }
            GC.SuppressFinalize(this);
            return ValueTask.CompletedTask;
        }
    }
}
