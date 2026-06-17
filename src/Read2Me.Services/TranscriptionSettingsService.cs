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
    /// CRUD + active-selection for transcription service configurations.
    /// Mirrors <see cref="LlmSettingsService"/>.
    /// </summary>
    public class TranscriptionSettingsService
    {
        private readonly IDbContextFactory<Read2MeDbContext> _dbFactory;
        private readonly ILogger<TranscriptionSettingsService> _logger;

        public event Action? OnChanged;

        public TranscriptionSettingsService(IDbContextFactory<Read2MeDbContext> dbFactory, ILogger<TranscriptionSettingsService> logger)
        {
            _dbFactory = dbFactory;
            _logger = logger;
        }

        public async Task<List<TranscriptionServiceConfig>> GetAllConfigsAsync()
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            return await db.TranscriptionServiceConfigs.OrderBy(c => c.Name).ToListAsync();
        }

        public async Task<int?> GetActiveConfigIdAsync()
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var settings = await db.Settings.SingleOrDefaultAsync();
            return settings?.ActiveTranscriptionConfigId;
        }

        public async Task<TranscriptionServiceConfig?> GetActiveConfigAsync()
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var settings = await db.Settings.SingleOrDefaultAsync();
            if (settings?.ActiveTranscriptionConfigId is not { } id)
                return null;
            return await db.TranscriptionServiceConfigs.FindAsync(id);
        }

        public async Task SetActiveConfigAsync(int configId)
        {
            _logger.LogInformation("Setting active transcription config to ID {ConfigId}", configId);
            await using var db = await _dbFactory.CreateDbContextAsync();
            await MutateSettingsAsync(db, s => s.ActiveTranscriptionConfigId = configId);
            await db.SaveChangesAsync();
            OnChanged?.Invoke();
        }

        public async Task<TranscriptionServiceConfig> CreateConfigAsync(TranscriptionServiceConfig config)
        {
            _logger.LogInformation("Creating transcription config '{Name}' (type: {Type})", config.Name, config.Type);
            await using var db = await _dbFactory.CreateDbContextAsync();
            config.Id = 0;
            db.TranscriptionServiceConfigs.Add(config);
            await db.SaveChangesAsync();

            var currentActiveId = (await db.Settings.SingleOrDefaultAsync())?.ActiveTranscriptionConfigId;
            if (currentActiveId == null)
            {
                _logger.LogDebug("Auto-activating transcription config '{Name}' (ID {Id}) — first config", config.Name, config.Id);
                await MutateSettingsAsync(db, s => s.ActiveTranscriptionConfigId = config.Id);
                await db.SaveChangesAsync();
            }

            OnChanged?.Invoke();
            return config;
        }

        public async Task UpdateConfigAsync(TranscriptionServiceConfig config)
        {
            _logger.LogInformation("Updating transcription config '{Name}' (ID {Id})", config.Name, config.Id);
            await using var db = await _dbFactory.CreateDbContextAsync();
            db.TranscriptionServiceConfigs.Update(config);
            await db.SaveChangesAsync();
            OnChanged?.Invoke();
        }

        public async Task DeleteConfigAsync(int configId)
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var config = await db.TranscriptionServiceConfigs.FindAsync(configId);
            if (config == null)
            {
                _logger.LogWarning("DeleteConfigAsync: transcription config ID {ConfigId} not found", configId);
                return;
            }

            _logger.LogInformation("Deleting transcription config '{Name}' (ID {Id})", config.Name, configId);
            db.TranscriptionServiceConfigs.Remove(config);
            await db.SaveChangesAsync();

            var settings = await db.Settings.SingleOrDefaultAsync();
            if (settings?.ActiveTranscriptionConfigId == configId)
            {
                var remaining = await db.TranscriptionServiceConfigs.OrderBy(c => c.Name).ToListAsync();
                if (remaining.Count == 1)
                {
                    _logger.LogDebug("Auto-activating sole remaining transcription config '{Name}' (ID {Id})", remaining[0].Name, remaining[0].Id);
                    settings.ActiveTranscriptionConfigId = remaining[0].Id;
                }
                else
                {
                    _logger.LogDebug("Clearing active transcription config — deleted config was active");
                    settings.ActiveTranscriptionConfigId = null;
                }
                await db.SaveChangesAsync();
            }
            OnChanged?.Invoke();
        }

        /// <summary>Loads the settings row, creating it if absent, and applies <paramref name="mutate"/>.</summary>
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
