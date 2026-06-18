using Microsoft.EntityFrameworkCore;
using Read2Me.AppData.Entities;

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

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<AppSettings>(e =>
            {
                e.Property(a => a.VoiceDesignSampleText).HasMaxLength(2000);
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
        }
    }
}
