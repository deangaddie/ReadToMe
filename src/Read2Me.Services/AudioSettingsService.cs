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
    /// CRUD + active-selection for voice-design audio server configurations.
    /// Mirrors LlmSettingsService. (Transcription configs are managed by
    /// <see cref="TranscriptionSettingsService"/>.)
    /// </summary>
    public class AudioSettingsService
    {
        private readonly IDbContextFactory<Read2MeDbContext> _dbFactory;
        private readonly ILogger<AudioSettingsService> _logger;

        public event Action? OnChanged;

        public AudioSettingsService(IDbContextFactory<Read2MeDbContext> dbFactory, ILogger<AudioSettingsService> logger)
        {
            _dbFactory = dbFactory;
            _logger = logger;
        }

        public async Task<List<AudioServerConfig>> GetAllConfigsAsync()
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            return await db.AudioServerConfigs.OrderBy(c => c.Name).ToListAsync();
        }

        public async Task<List<AudioServerConfig>> GetConfigsByRoleAsync(AudioServerRole role)
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            return await db.AudioServerConfigs
                .Where(c => c.Role == role)
                .OrderBy(c => c.Name)
                .ToListAsync();
        }

        public async Task<AudioServerConfig?> GetActiveVoiceDesignConfigAsync()
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var settings = await db.Settings.SingleOrDefaultAsync();
            if (settings?.ActiveVoiceDesignConfigId is not { } id) return null;
            return await db.AudioServerConfigs.FindAsync(id);
        }

        public async Task SetActiveVoiceDesignConfigAsync(int configId)
        {
            _logger.LogInformation("Setting active voice design config to ID {ConfigId}", configId);
            await using var db = await _dbFactory.CreateDbContextAsync();
            await MutateSettingsAsync(db, s => s.ActiveVoiceDesignConfigId = configId);
            await db.SaveChangesAsync();
            OnChanged?.Invoke();
        }

        public async Task<AudioServerConfig> CreateConfigAsync(AudioServerConfig config)
        {
            _logger.LogInformation("Creating audio config '{Name}' (role: {Role})", config.Name, config.Role);
            await using var db = await _dbFactory.CreateDbContextAsync();
            config.Id = 0;
            db.AudioServerConfigs.Add(config);
            await db.SaveChangesAsync();

            var settings = await db.Settings.SingleOrDefaultAsync();
            if (config.Role == AudioServerRole.VoiceDesign && settings?.ActiveVoiceDesignConfigId == null)
            {
                _logger.LogDebug("Auto-activating voice design config '{Name}'", config.Name);
                await MutateSettingsAsync(db, s => s.ActiveVoiceDesignConfigId = config.Id);
                await db.SaveChangesAsync();
            }

            OnChanged?.Invoke();
            return config;
        }

        public async Task UpdateConfigAsync(AudioServerConfig config)
        {
            _logger.LogInformation("Updating audio config '{Name}' (ID {Id})", config.Name, config.Id);
            await using var db = await _dbFactory.CreateDbContextAsync();
            db.AudioServerConfigs.Update(config);
            await db.SaveChangesAsync();
            OnChanged?.Invoke();
        }

        public async Task DeleteConfigAsync(int configId)
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var config = await db.AudioServerConfigs.FindAsync(configId);
            if (config == null)
            {
                _logger.LogWarning("DeleteConfigAsync: audio config ID {ConfigId} not found", configId);
                return;
            }

            _logger.LogInformation("Deleting audio config '{Name}' (ID {Id})", config.Name, configId);
            var role = config.Role;
            db.AudioServerConfigs.Remove(config);
            await db.SaveChangesAsync();

            var settings = await db.Settings.SingleOrDefaultAsync();
            if (settings == null) return;

            bool changed = false;
            if (role == AudioServerRole.VoiceDesign && settings.ActiveVoiceDesignConfigId == configId)
            {
                var remaining = await db.AudioServerConfigs
                    .Where(c => c.Role == AudioServerRole.VoiceDesign)
                    .OrderBy(c => c.Name).ToListAsync();
                settings.ActiveVoiceDesignConfigId = remaining.Count == 1 ? remaining[0].Id : null;
                changed = true;
            }

            if (changed) await db.SaveChangesAsync();
            OnChanged?.Invoke();
        }

        private static async Task MutateSettingsAsync(Read2MeDbContext db, Action<AppSettings> mutate)
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
