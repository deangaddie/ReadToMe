using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Read2Me.AppData.Entities;
using System.Text.Json;

namespace Read2Me.AppData
{
    public class Read2MeDbContext : DbContext
    {
        public Read2MeDbContext(DbContextOptions<Read2MeDbContext> options) : base(options)
        {
        }

        public DbSet<AppTheme> Themes => Set<AppTheme>();
        public DbSet<AppSettings> Settings => Set<AppSettings>();
        public DbSet<LlmServerConfig> LlmServerConfigs => Set<LlmServerConfig>();
        public DbSet<LlmPromptSettings> PromptSettings => Set<LlmPromptSettings>();
        public DbSet<VoiceDesignServiceConfig> VoiceDesignServiceConfigs => Set<VoiceDesignServiceConfig>();
        public DbSet<TranscriptionServiceConfig> TranscriptionServiceConfigs => Set<TranscriptionServiceConfig>();
        public DbSet<ParagraphTtsServiceConfig> ParagraphTtsServiceConfigs => Set<ParagraphTtsServiceConfig>();
        public DbSet<TextSubstitutionStep> TextSubstitutionSteps => Set<TextSubstitutionStep>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<AppSettings>(e =>
            {
                e.Property(a => a.VoiceDesignSampleText).HasMaxLength(2000);
                e.Property(a => a.FfmpegPath).HasMaxLength(1024);
                e.Property(a => a.WerThreshold).HasDefaultValue(0.15);
            });

            modelBuilder.Entity<VoiceDesignServiceConfig>(e =>
            {
                e.HasKey(a => a.Id);
                e.Property(a => a.Name).HasMaxLength(250).IsRequired();
                e.Property(a => a.Type).HasConversion<string>().IsRequired();
                e.Property(a => a.SettingsJson).IsRequired();
            });

            modelBuilder.Entity<TranscriptionServiceConfig>(e =>
            {
                e.HasKey(a => a.Id);
                e.Property(a => a.Name).HasMaxLength(250).IsRequired();
                e.Property(a => a.Type).HasConversion<string>().IsRequired();
                e.Property(a => a.SettingsJson).IsRequired();
            });

            modelBuilder.Entity<ParagraphTtsServiceConfig>(e =>
            {
                e.HasKey(a => a.Id);
                e.Property(a => a.Name).HasMaxLength(250).IsRequired();
                e.Property(a => a.Type).HasConversion<string>().IsRequired();
                e.Property(a => a.SettingsJson).IsRequired();
                e.Property(a => a.EnabledStepIds)
                    .HasColumnType("TEXT")
                    .HasDefaultValueSql("'[]'")
                    .HasConversion(
                        new ValueConverter<List<string>, string>(
                            v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                            v => string.IsNullOrEmpty(v)
                                ? new List<string>()
                                : JsonSerializer.Deserialize<List<string>>(v, (JsonSerializerOptions?)null) ?? new List<string>()),
                        new ValueComparer<List<string>>(
                            (a, b) => a != null && b != null && a.SequenceEqual(b),
                            v => v.Aggregate(0, (h, s) => HashCode.Combine(h, s.GetHashCode())),
                            v => v.ToList()));
            });

            modelBuilder.Entity<TextSubstitutionStep>(e =>
            {
                e.HasKey(s => s.Id);
                e.Property(s => s.FromText).IsRequired();
                e.Property(s => s.ToText).IsRequired();
                e.HasOne(s => s.Config)
                    .WithMany(c => c.SubstitutionSteps)
                    .HasForeignKey(s => s.ParagraphTtsServiceConfigId)
                    .OnDelete(DeleteBehavior.Cascade);
                e.HasIndex(s => new { s.ParagraphTtsServiceConfigId, s.Order });
            });
        }
    }
}
