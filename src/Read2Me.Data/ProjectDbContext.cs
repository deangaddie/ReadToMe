using Microsoft.EntityFrameworkCore;
using Read2Me.Data.Entities;

namespace Read2Me.Data
{
    public class ProjectDbContext : DbContext
    {
        public static readonly Guid NarratorId = new("00000000-0000-0000-0000-000000000001");

        public ProjectDbContext(DbContextOptions<ProjectDbContext> options) : base(options) { }

        public DbSet<Project> Projects => Set<Project>();
        public DbSet<Character> Characters => Set<Character>();
        public DbSet<Volume> Volumes => Set<Volume>();
        public DbSet<Part> Parts => Set<Part>();
        public DbSet<Chapter> Chapters => Set<Chapter>();
        public DbSet<Paragraph> Paragraphs => Set<Paragraph>();
        public DbSet<ParagraphItem> ParagraphItems => Set<ParagraphItem>();
        public DbSet<Voice> Voices => Set<Voice>();
        public DbSet<CharacterAlias> CharacterAliases => Set<CharacterAlias>();
        public DbSet<AudioReview> AudioReviews => Set<AudioReview>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Project>(e =>
            {
                e.HasKey(p => p.Id);
                e.Property(p => p.Title).HasMaxLength(250).IsRequired();
                e.Property(p => p.BookTitle).HasMaxLength(250).IsRequired();
                e.Property(p => p.Author).HasMaxLength(250).IsRequired();
                e.Property(p => p.Filename).HasMaxLength(526).IsRequired();
                e.Property(p => p.Type).HasConversion<string>().IsRequired();
            });

            modelBuilder.Entity<Character>(e =>
            {
                e.HasKey(c => c.Id);
                e.Property(c => c.Name).HasMaxLength(250).IsRequired();
                e.HasData(new Character { Id = NarratorId, Name = "Narrator", IsNarrator = true });
            });

            modelBuilder.Entity<Volume>(e =>
            {
                e.HasKey(v => v.Id);
                e.Property(v => v.Title).HasMaxLength(250).IsRequired();
                e.Property(v => v.Order).HasMaxLength(250).IsRequired().UseCollation("BINARY");
            });

            modelBuilder.Entity<Part>(e =>
            {
                e.HasKey(p => p.Id);
                e.Property(p => p.Title).HasMaxLength(250);
                e.Property(p => p.Order).HasMaxLength(250).IsRequired().UseCollation("BINARY");
                e.HasOne(p => p.Volume).WithMany(v => v.Parts).HasForeignKey(p => p.VolumeId);
            });

            modelBuilder.Entity<Chapter>(e =>
            {
                e.HasKey(c => c.Id);
                e.Property(c => c.Title).HasMaxLength(250);
                e.Property(c => c.Order).HasMaxLength(250).IsRequired().UseCollation("BINARY");
                e.HasOne(c => c.Part).WithMany(p => p.Chapters).HasForeignKey(c => c.PartId);
            });

            modelBuilder.Entity<Paragraph>(e =>
            {
                e.HasKey(p => p.Id);
                e.Property(p => p.Order).HasMaxLength(250).IsRequired().UseCollation("BINARY");
                e.HasOne(p => p.Chapter).WithMany(c => c.Paragraphs).HasForeignKey(p => p.ChapterId);
                e.HasOne(p => p.Character).WithMany().HasForeignKey(p => p.CharacterId);
            });

            modelBuilder.Entity<ParagraphItem>(e =>
            {
                e.HasKey(pi => pi.Id);
                e.Property(pi => pi.Order).HasMaxLength(250).IsRequired().UseCollation("BINARY");
                e.Property(pi => pi.ItemType).HasConversion<string>().IsRequired();
                e.Property(pi => pi.VoiceInstructions).HasMaxLength(3000);
                e.Property(pi => pi.AudioFileName).HasMaxLength(512);
                e.HasOne(pi => pi.Paragraph).WithMany(p => p.Items).HasForeignKey(pi => pi.ParagraphId);
                e.HasOne(pi => pi.Character).WithMany().HasForeignKey(pi => pi.CharacterId);
            });

            modelBuilder.Entity<Voice>(e =>
            {
                e.HasKey(v => v.Id);
                e.Property(v => v.Name).HasMaxLength(250).IsRequired();
                e.Property(v => v.Description).HasMaxLength(1000);
                e.Property(v => v.Source).HasConversion<string>().IsRequired();
                e.Property(v => v.DesignPrompt).HasMaxLength(4000);
                e.Property(v => v.Transcript).HasMaxLength(4000);
                e.Property(v => v.AudioFileName).HasMaxLength(512);
                e.Property(v => v.SettingsOverrideJson).HasMaxLength(4000);
                e.HasOne(v => v.Character).WithMany(c => c.Voices).HasForeignKey(v => v.CharacterId);
            });

            modelBuilder.Entity<CharacterAlias>(e =>
            {
                e.HasKey(a => a.Id);
                e.Property(a => a.Name).HasMaxLength(250).IsRequired();
                e.HasOne(a => a.Character).WithMany(c => c.Aliases).HasForeignKey(a => a.CharacterId);
                e.HasIndex(a => new { a.CharacterId, a.Name }).IsUnique();
            });

            modelBuilder.Entity<AudioReview>(e =>
            {
                e.HasKey(r => r.Id);
                e.Property(r => r.State).HasConversion<string>().IsRequired();
                e.Property(r => r.NormalizeReason).HasMaxLength(500);
                e.Property(r => r.VerifyReason).HasMaxLength(500);
                e.Property(r => r.Transcript).HasMaxLength(8000);
                e.Property(r => r.OriginalTextSnapshot).HasMaxLength(8000);
                e.HasOne(r => r.ParagraphItem).WithMany().HasForeignKey(r => r.ParagraphItemId);
                e.HasIndex(r => r.ParagraphItemId).IsUnique();
            });
        }
    }
}
