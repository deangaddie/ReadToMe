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
                e.HasOne(pi => pi.Paragraph).WithMany(p => p.Items).HasForeignKey(pi => pi.ParagraphId);
                e.HasOne(pi => pi.Character).WithMany().HasForeignKey(pi => pi.CharacterId);
            });

            modelBuilder.Entity<Voice>(e =>
            {
                e.HasKey(v => v.Id);
                e.Property(v => v.Title).HasMaxLength(250).IsRequired();
                e.HasOne(v => v.Character).WithMany(c => c.Voices).HasForeignKey(v => v.CharacterId);
            });
        }
    }
}
