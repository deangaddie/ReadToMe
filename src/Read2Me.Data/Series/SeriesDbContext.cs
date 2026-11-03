using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Read2Me.Data.Series
{
    /// <summary>
    /// One series' registry, <c>{workspace}/.series/{seriesId}/series.db</c> (ADR 0018). It holds the
    /// series' name, its members by project Id with their Series positions, and its Series characters
    /// with the <c>(projectId, characterId)</c> pairs linked to each. Membership and links are
    /// recorded only here, never in a book's <c>project.db</c>, and nothing in it has History.
    /// </summary>
    public class SeriesDbContext(DbContextOptions<SeriesDbContext> options) : DbContext(options)
    {
        public const string DbFileName = "series.db";

        public DbSet<SeriesRow> Series => Set<SeriesRow>();
        public DbSet<SeriesBook> Books => Set<SeriesBook>();
        public DbSet<SeriesCharacterRow> Characters => Set<SeriesCharacterRow>();
        public DbSet<SeriesLink> Links => Set<SeriesLink>();

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

            modelBuilder.Entity<SeriesCharacterRow>(e =>
            {
                e.ToTable("SeriesCharacters");
                e.HasKey(c => c.Id);
            });

            modelBuilder.Entity<SeriesLink>(e =>
            {
                // A book links at most one Character to a Series character...
                e.HasKey(l => new { l.SeriesCharacterId, l.ProjectId });
                // ...and a Character is linked to at most one Series character.
                e.HasIndex(l => new { l.ProjectId, l.CharacterId }).IsUnique();
                e.HasOne<SeriesCharacterRow>().WithMany().HasForeignKey(l => l.SeriesCharacterId)
                    .OnDelete(DeleteBehavior.Cascade);
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

    /// <summary>
    /// A Series character (ADR 0018): only an identity. Its label is derived live from the linked
    /// Characters, and its Voices live in each linked book.
    /// </summary>
    public class SeriesCharacterRow
    {
        public Guid Id { get; set; }
    }

    /// <summary>A book's Character linked to a Series character, by project Id and Character Id.</summary>
    public class SeriesLink
    {
        public Guid SeriesCharacterId { get; set; }
        public Guid ProjectId { get; set; }
        public Guid CharacterId { get; set; }
    }

    public class SeriesDbContextDesignFactory : IDesignTimeDbContextFactory<SeriesDbContext>
    {
        public SeriesDbContext CreateDbContext(string[] args) => new(
            new DbContextOptionsBuilder<SeriesDbContext>().UseSqlite("Data Source=series.db;Pooling=false").Options);
    }
}
