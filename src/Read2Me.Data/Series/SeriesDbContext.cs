using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Read2Me.Data.Series
{
    /// <summary>
    /// One series' registry, <c>{workspace}/.series/{seriesId}/series.db</c> (ADR 0018). It holds the
    /// series' name and its members by project Id with their Series positions. Membership is recorded
    /// only here, never in a book's <c>project.db</c>, and nothing in it has History.
    /// </summary>
    public class SeriesDbContext(DbContextOptions<SeriesDbContext> options) : DbContext(options)
    {
        public const string DbFileName = "series.db";

        public DbSet<SeriesRow> Series => Set<SeriesRow>();
        public DbSet<SeriesBook> Books => Set<SeriesBook>();

        /// <summary>Opens (creating and migrating as needed) the registry in <paramref name="folderPath"/>.</summary>
        public static async Task<SeriesDbContext> OpenAsync(string folderPath, CancellationToken ct = default)
        {
            Directory.CreateDirectory(folderPath);
            var options = new DbContextOptionsBuilder<SeriesDbContext>()
                // No pooling: a deleted series' folder must not be held open by an idle connection.
                .UseSqlite($"Data Source={Path.Combine(folderPath, DbFileName)};Pooling=false")
                .Options;
            var db = new SeriesDbContext(options);
            await db.Database.MigrateAsync(ct);
            return db;
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<SeriesRow>(e =>
            {
                e.ToTable("Series");
                e.HasKey(s => s.Id);
                e.Property(s => s.Name).HasMaxLength(250).IsRequired();
            });

            modelBuilder.Entity<SeriesBook>(e =>
            {
                e.HasKey(b => b.ProjectId);
                // Stored as text: exact, with no upper bound to fit; books are sorted in memory.
                e.Property(b => b.Position).IsRequired();
            });
        }
    }

    /// <summary>The registry's single row: who the series is.</summary>
    public class SeriesRow
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
    }

    /// <summary>A member book, by its project's Id (folders can be renamed by hand; the Id can't).</summary>
    public class SeriesBook
    {
        public Guid ProjectId { get; set; }
        public decimal Position { get; set; }
    }

    public class SeriesDbContextDesignFactory : IDesignTimeDbContextFactory<SeriesDbContext>
    {
        public SeriesDbContext CreateDbContext(string[] args) => new(
            new DbContextOptionsBuilder<SeriesDbContext>().UseSqlite("Data Source=series.db;Pooling=false").Options);
    }
}
