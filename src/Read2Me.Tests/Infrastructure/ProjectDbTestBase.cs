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
            var db = OpenUnmigratedDb();
            await db.Database.MigrateAsync();
            return db;
        }

        /// <summary>
        /// Who the persisted Book says speaks an item. The question "did that write actually
        /// commit?" reduces to this often enough — across the projection, presenter and mutation
        /// fixtures — that asking it through a fresh context belongs here rather than in each.
        /// </summary>
        protected async Task<Guid?> PersistedSpeakerOfAsync(Guid paragraphItemId)
        {
            await using var db = await OpenDbAsync();
            return (await db.ParagraphItems.FindAsync(paragraphItemId))!.CharacterId;
        }

        /// <summary>
        /// Opens the same database without migrating it — for tests that drive the migrator
        /// themselves (e.g. migrate to an older migration, seed, then migrate up).
        /// Caller owns dispose.
        /// </summary>
        protected ProjectDbContext OpenUnmigratedDb() => new(
            new DbContextOptionsBuilder<ProjectDbContext>()
                .UseSqlite($"Data Source={Path.Combine(FolderPath, "project.db")};Pooling=false")
                .Options);

        public virtual ValueTask DisposeAsync()
        {
            try { if (Directory.Exists(TempDir)) Directory.Delete(TempDir, recursive: true); }
            catch { /* best effort */ }
            GC.SuppressFinalize(this);
            return ValueTask.CompletedTask;
        }
    }
}
