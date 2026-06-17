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
        public DbSet<AudioServerConfig> AudioServerConfigs => Set<AudioServerConfig>();
        public DbSet<TranscriptionServiceConfig> TranscriptionServiceConfigs => Set<TranscriptionServiceConfig>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<AudioServerConfig>(e =>
            {
                e.HasKey(a => a.Id);
                e.Property(a => a.Name).HasMaxLength(250).IsRequired();
                e.Property(a => a.BaseUrl).HasMaxLength(512).IsRequired();
                e.Property(a => a.ApiKey).HasMaxLength(512);
                e.Property(a => a.Model).HasMaxLength(250);
                e.Property(a => a.Role).HasConversion<string>().IsRequired();
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
