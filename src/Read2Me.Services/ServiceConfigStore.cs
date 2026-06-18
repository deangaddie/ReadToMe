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
    /// Deep module: CRUD + active-selection for any service config type, parameterised over
    /// the DbSet selector and the AppSettings active-id field getter/setter.
    /// </summary>
    public sealed class ServiceConfigStore<TConfig> where TConfig : class
    {
        private readonly IDbContextFactory<Read2MeDbContext> _dbFactory;
        private readonly ILogger _logger;
        private readonly Func<Read2MeDbContext, DbSet<TConfig>> _setSelector;
        private readonly Func<AppSettings, int?> _getActiveId;
        private readonly Action<AppSettings, int?> _setActiveId;
        private readonly Func<TConfig, int> _getId;
        private readonly Action<TConfig, int> _setId;
        private readonly string _typeName;

        public event Action? OnChanged;

        public ServiceConfigStore(
            IDbContextFactory<Read2MeDbContext> dbFactory,
            ILogger logger,
            Func<Read2MeDbContext, DbSet<TConfig>> setSelector,
            Func<AppSettings, int?> getActiveId,
            Action<AppSettings, int?> setActiveId,
            Func<TConfig, int> getId,
            Action<TConfig, int> setId,
            string typeName,
            Func<TConfig, string>? nameSelector = null)
        {
            _dbFactory = dbFactory;
            _logger = logger;
            _setSelector = setSelector;
            _getActiveId = getActiveId;
            _setActiveId = setActiveId;
            _getId = getId;
            _setId = setId;
            _typeName = typeName;
        }

        public async Task<List<TConfig>> GetAllConfigsAsync()
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            return await _setSelector(db).OrderBy(c => EF.Property<string>(c, "Name")).ToListAsync();
        }

        public async Task<int?> GetActiveConfigIdAsync()
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var settings = await db.Settings.SingleOrDefaultAsync();
            return settings == null ? null : _getActiveId(settings);
        }

        public async Task<TConfig?> GetActiveConfigAsync()
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var settings = await db.Settings.SingleOrDefaultAsync();
            if (settings == null) return null;
            var id = _getActiveId(settings);
            if (id == null) return null;
            return await _setSelector(db).FindAsync(id.Value);
        }

        public async Task SetActiveConfigAsync(int configId)
        {
            _logger.LogInformation("Setting active {Type} config to ID {ConfigId}", _typeName, configId);
            await using var db = await _dbFactory.CreateDbContextAsync();
            await MutateSettingsAsync(db, s => _setActiveId(s, configId));
            await db.SaveChangesAsync();
            OnChanged?.Invoke();
        }

        public async Task<TConfig> CreateConfigAsync(TConfig config)
        {
            _logger.LogInformation("Creating {Type} config", _typeName);
            await using var db = await _dbFactory.CreateDbContextAsync();
            _setId(config, 0);
            _setSelector(db).Add(config);
            await db.SaveChangesAsync();

            var currentActiveId = (await db.Settings.SingleOrDefaultAsync()) is { } s ? _getActiveId(s) : null;
            if (currentActiveId == null)
            {
                var newId = _getId(config);
                _logger.LogDebug("Auto-activating {Type} config (ID {Id}) — first config", _typeName, newId);
                await MutateSettingsAsync(db, s => _setActiveId(s, newId));
                await db.SaveChangesAsync();
            }

            OnChanged?.Invoke();
            return config;
        }

        public async Task UpdateConfigAsync(TConfig config)
        {
            _logger.LogInformation("Updating {Type} config (ID {Id})", _typeName, _getId(config));
            await using var db = await _dbFactory.CreateDbContextAsync();
            _setSelector(db).Update(config);
            await db.SaveChangesAsync();
            OnChanged?.Invoke();
        }

        public async Task DeleteConfigAsync(int configId)
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var config = await _setSelector(db).FindAsync(configId);
            if (config == null)
            {
                _logger.LogWarning("DeleteConfigAsync: {Type} config ID {ConfigId} not found", _typeName, configId);
                return;
            }

            _logger.LogInformation("Deleting {Type} config (ID {Id})", _typeName, configId);
            _setSelector(db).Remove(config);
            await db.SaveChangesAsync();

            var settings = await db.Settings.SingleOrDefaultAsync();
            if (settings != null && _getActiveId(settings) == configId)
            {
                var remaining = await _setSelector(db).OrderBy(c => EF.Property<string>(c, "Name")).ToListAsync();
                if (remaining.Count == 1)
                {
                    var survivorId = _getId(remaining[0]);
                    _logger.LogDebug("Auto-activating sole remaining {Type} config (ID {Id})", _typeName, survivorId);
                    _setActiveId(settings, survivorId);
                }
                else
                {
                    _logger.LogDebug("Clearing active {Type} config — deleted config was active", _typeName);
                    _setActiveId(settings, null);
                }
                await db.SaveChangesAsync();
            }
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
