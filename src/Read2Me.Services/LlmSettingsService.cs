using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Read2Me.AppData;
using Read2Me.AppData.Entities;

namespace Read2Me.Services
{
    /// <summary>
    /// CRUD + active-selection for LLM server configurations. Mirrors ThemeService.
    /// </summary>
    public class LlmSettingsService
    {
        private readonly IDbContextFactory<Read2MeDbContext> _dbFactory;
        private readonly ILogger<LlmSettingsService> _logger;

        public event Action? OnChanged;

        public LlmSettingsService(IDbContextFactory<Read2MeDbContext> dbFactory, ILogger<LlmSettingsService> logger)
        {
            _dbFactory = dbFactory;
            _logger = logger;
        }

        public async Task<List<LlmServerConfig>> GetAllConfigsAsync()
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            return await db.LlmServerConfigs.OrderBy(c => c.Name).ToListAsync();
        }

        public async Task<int?> GetActiveConfigIdAsync()
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var settings = await db.Settings.SingleOrDefaultAsync();
            return settings?.ActiveLlmConfigId;
        }

        public async Task<LlmServerConfig?> GetActiveConfigAsync()
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var settings = await db.Settings.SingleOrDefaultAsync();
            if (settings?.ActiveLlmConfigId is not { } id)
                return null;
            return await db.LlmServerConfigs.FindAsync(id);
        }

        public async Task SetActiveConfigAsync(int configId)
        {
            _logger.LogInformation("Setting active LLM config to ID {ConfigId}", configId);
            await using var db = await _dbFactory.CreateDbContextAsync();
            await MutateSettingsAsync(db, s => s.ActiveLlmConfigId = configId);
            await db.SaveChangesAsync();
            OnChanged?.Invoke();
        }

        public async Task<LlmServerConfig> CreateConfigAsync(LlmServerConfig config)
        {
            _logger.LogInformation("Creating LLM config '{Name}'", config.Name);
            await using var db = await _dbFactory.CreateDbContextAsync();
            config.Id = 0;
            db.LlmServerConfigs.Add(config);
            await db.SaveChangesAsync();

            var currentActiveId = (await db.Settings.SingleOrDefaultAsync())?.ActiveLlmConfigId;
            if (currentActiveId == null)
            {
                _logger.LogDebug("Auto-activating LLM config '{Name}' (ID {Id}) — first config", config.Name, config.Id);
                await MutateSettingsAsync(db, s => s.ActiveLlmConfigId = config.Id);
                await db.SaveChangesAsync();
            }

            OnChanged?.Invoke();
            return config;
        }

        public async Task UpdateConfigAsync(LlmServerConfig config)
        {
            _logger.LogInformation("Updating LLM config '{Name}' (ID {Id})", config.Name, config.Id);
            await using var db = await _dbFactory.CreateDbContextAsync();
            db.LlmServerConfigs.Update(config);
            await db.SaveChangesAsync();
            OnChanged?.Invoke();
        }

        public async Task DeleteConfigAsync(int configId)
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var config = await db.LlmServerConfigs.FindAsync(configId);
            if (config == null)
            {
                _logger.LogWarning("DeleteConfigAsync: LLM config ID {ConfigId} not found", configId);
                return;
            }

            _logger.LogInformation("Deleting LLM config '{Name}' (ID {Id})", config.Name, configId);
            db.LlmServerConfigs.Remove(config);
            await db.SaveChangesAsync();

            var settings = await db.Settings.SingleOrDefaultAsync();
            if (settings?.ActiveLlmConfigId == configId)
            {
                var remaining = await db.LlmServerConfigs.OrderBy(c => c.Name).ToListAsync();
                if (remaining.Count == 1)
                {
                    _logger.LogDebug("Auto-activating sole remaining LLM config '{Name}' (ID {Id})", remaining[0].Name, remaining[0].Id);
                    settings.ActiveLlmConfigId = remaining[0].Id;
                }
                else
                {
                    _logger.LogDebug("Clearing active LLM config — deleted config was active");
                    settings.ActiveLlmConfigId = null;
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
