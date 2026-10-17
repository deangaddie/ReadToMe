using Microsoft.EntityFrameworkCore;
using Read2Me.Data;
using Read2Me.Data.Entities;
using Read2Me.Data.Enums;

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
        /// commit?" reduces to this often enough — across the projection and mutation
        /// fixtures — that asking it through a fresh context belongs here rather than in each.
        /// </summary>
        protected async Task<Guid?> PersistedSpeakerOfAsync(Guid paragraphItemId)
        {
            await using var db = await OpenDbAsync();
            return (await db.ParagraphItems.FindAsync(paragraphItemId))!.CharacterId;
        }

        /// <summary>
        /// A checkpoint row written straight to the Book — arrange-side only, so a test can start from
        /// a stale row, which no gesture produces directly.
        /// </summary>
        protected async Task SeedCheckpointAsync(
            PipelineStep step, CheckpointState state, bool stale = false, DateTimeOffset? at = null)
        {
            await using var db = await OpenDbAsync();
            db.StepCheckpoints.Add(new StepCheckpoint
            {
                Step = step, State = state, Stale = stale, At = at ?? DateTimeOffset.UnixEpoch,
            });
            await db.SaveChangesAsync();
        }

        /// <summary>The steps that hold a checkpoint row, in Pipeline order.</summary>
        protected async Task<List<StepCheckpoint>> CheckpointRowsAsync()
        {
            await using var db = await OpenDbAsync();
            // In memory: the step is stored as text, so SQL would sort it alphabetically.
            return (await db.StepCheckpoints.ToListAsync()).OrderBy(c => c.Step).ToList();
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
