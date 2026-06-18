using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Read2Me.AppData;
using Read2Me.AppData.Entities;

namespace Read2Me.Services
{
    /// <summary>
    /// CRUD + active-selection for voice-design service configurations.
    /// Mirrors <see cref="TranscriptionSettingsService"/>.
    /// </summary>
    public class VoiceDesignSettingsService
    {
        private readonly IDbContextFactory<Read2MeDbContext> _dbFactory;
        private readonly ILogger<VoiceDesignSettingsService> _logger;

        public event Action? OnChanged;

        public VoiceDesignSettingsService(IDbContextFactory<Read2MeDbContext> dbFactory, ILogger<VoiceDesignSettingsService> logger)
        {
            _dbFactory = dbFactory;
            _logger = logger;
        }

        public async Task<List<VoiceDesignServiceConfig>> GetAllConfigsAsync()
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            return await db.VoiceDesignServiceConfigs.OrderBy(c => c.Name).ToListAsync();
        }

        public async Task<int?> GetActiveConfigIdAsync()
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var settings = await db.Settings.SingleOrDefaultAsync();
            return settings?.ActiveVoiceDesignConfigId;
        }

        public async Task<VoiceDesignServiceConfig?> GetActiveConfigAsync()
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var settings = await db.Settings.SingleOrDefaultAsync();
            if (settings?.ActiveVoiceDesignConfigId is not { } id)
                return null;
            return await db.VoiceDesignServiceConfigs.FindAsync(id);
        }

        public async Task SetActiveConfigAsync(int configId)
        {
            _logger.LogInformation("Setting active voice design config to ID {ConfigId}", configId);
            await using var db = await _dbFactory.CreateDbContextAsync();
            await MutateSettingsAsync(db, s => s.ActiveVoiceDesignConfigId = configId);
            await db.SaveChangesAsync();
            OnChanged?.Invoke();
        }

        public async Task<VoiceDesignServiceConfig> CreateConfigAsync(VoiceDesignServiceConfig config)
        {
            _logger.LogInformation("Creating voice design config '{Name}' (type: {Type})", config.Name, config.Type);
            await using var db = await _dbFactory.CreateDbContextAsync();
            config.Id = 0;
            db.VoiceDesignServiceConfigs.Add(config);
            await db.SaveChangesAsync();

            var currentActiveId = (await db.Settings.SingleOrDefaultAsync())?.ActiveVoiceDesignConfigId;
            if (currentActiveId == null)
            {
                _logger.LogDebug("Auto-activating voice design config '{Name}' (ID {Id}) — first config", config.Name, config.Id);
                await MutateSettingsAsync(db, s => s.ActiveVoiceDesignConfigId = config.Id);
                await db.SaveChangesAsync();
            }

            OnChanged?.Invoke();
            return config;
        }

        public async Task UpdateConfigAsync(VoiceDesignServiceConfig config)
        {
            _logger.LogInformation("Updating voice design config '{Name}' (ID {Id})", config.Name, config.Id);
            await using var db = await _dbFactory.CreateDbContextAsync();
            db.VoiceDesignServiceConfigs.Update(config);
            await db.SaveChangesAsync();
            OnChanged?.Invoke();
        }

        public async Task DeleteConfigAsync(int configId)
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var config = await db.VoiceDesignServiceConfigs.FindAsync(configId);
            if (config == null)
            {
                _logger.LogWarning("DeleteConfigAsync: voice design config ID {ConfigId} not found", configId);
                return;
            }

            _logger.LogInformation("Deleting voice design config '{Name}' (ID {Id})", config.Name, configId);
            db.VoiceDesignServiceConfigs.Remove(config);
            await db.SaveChangesAsync();

            var settings = await db.Settings.SingleOrDefaultAsync();
            if (settings?.ActiveVoiceDesignConfigId == configId)
            {
                var remaining = await db.VoiceDesignServiceConfigs.OrderBy(c => c.Name).ToListAsync();
                if (remaining.Count == 1)
                {
                    _logger.LogDebug("Auto-activating sole remaining voice design config '{Name}' (ID {Id})", remaining[0].Name, remaining[0].Id);
                    settings.ActiveVoiceDesignConfigId = remaining[0].Id;
                }
                else
                {
                    _logger.LogDebug("Clearing active voice design config — deleted config was active");
                    settings.ActiveVoiceDesignConfigId = null;
                }
                await db.SaveChangesAsync();
            }
            OnChanged?.Invoke();
        }

        public async Task<string?> GetSampleTextAsync()
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var settings = await db.Settings.SingleOrDefaultAsync();
            return settings?.VoiceDesignSampleText;
        }

        public async Task SetSampleTextAsync(string? sampleText)
        {
            _logger.LogInformation("Updating voice design sample text");
            await using var db = await _dbFactory.CreateDbContextAsync();
            await MutateSettingsAsync(db, s => s.VoiceDesignSampleText = sampleText);
            await db.SaveChangesAsync();
            OnChanged?.Invoke();
        }

        /// <summary>Loads the settings row, creating it if absent, and applies <paramref name="mutate"/>.</summary>
        public static async Task MutateSettingsAsync(Read2MeDbContext db, Action<AppSettings> mutate)
        {
            var settings = await db.Settings.SingleOrDefaultAsync();
            if (settings == null)
            {
                settings = new AppSettings();
                db.Settings.Add(settings);
            }
            mutate(settings);
        }
    }
}
