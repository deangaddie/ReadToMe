using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Read2Me.AppData;
using Read2Me.AppData.Entities;
using Read2Me.Services.Events;

namespace Read2Me.Services
{
    /// <summary>
    /// CRUD + active-selection for voice-design service configurations.
    /// </summary>
    public class VoiceDesignSettingsService
    {
        private readonly ServiceConfigStore<VoiceDesignServiceConfig> _store;
        private readonly IDbContextFactory<Read2MeDbContext> _dbFactory;

        public event Action? OnChanged;

        private readonly EventBroadcaster<SettingsChanged>? _changes;

        private void NotifyChanged()
        {
            OnChanged?.Invoke();
            _changes?.Publish(new SettingsChanged(SettingsArea.VoiceDesign));
        }

        /// <summary>Without the process-wide change signal (tests, and NSubstitute class proxies, use this arity).</summary>
        public VoiceDesignSettingsService(IDbContextFactory<Read2MeDbContext> dbFactory, ILogger<VoiceDesignSettingsService> logger)
            : this(dbFactory, logger, null) { }

        public VoiceDesignSettingsService(IDbContextFactory<Read2MeDbContext> dbFactory, ILogger<VoiceDesignSettingsService> logger,
            EventBroadcaster<SettingsChanged>? changes)
        {
            _changes = changes;
            _dbFactory = dbFactory;
            _store = new ServiceConfigStore<VoiceDesignServiceConfig>(
                dbFactory, logger,
                db => db.VoiceDesignServiceConfigs,
                s => s.ActiveVoiceDesignConfigId,
                (s, id) => s.ActiveVoiceDesignConfigId = id,
                c => c.Id,
                (c, id) => c.Id = id,
                "VoiceDesign");
            _store.OnChanged += () => NotifyChanged();
        }

        public Task<List<VoiceDesignServiceConfig>> GetAllConfigsAsync() => _store.GetAllConfigsAsync();
        public Task<int?> GetActiveConfigIdAsync() => _store.GetActiveConfigIdAsync();
        public virtual Task<VoiceDesignServiceConfig?> GetActiveConfigAsync() => _store.GetActiveConfigAsync();
        public Task SetActiveConfigAsync(int configId) => _store.SetActiveConfigAsync(configId);
        public Task<VoiceDesignServiceConfig> CreateConfigAsync(VoiceDesignServiceConfig config) => _store.CreateConfigAsync(config);
        public Task UpdateConfigAsync(VoiceDesignServiceConfig config) => _store.UpdateConfigAsync(config);
        public Task DeleteConfigAsync(int configId) => _store.DeleteConfigAsync(configId);

        public virtual async Task<string?> GetSampleTextAsync()
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var settings = await db.Settings.SingleOrDefaultAsync();
            return settings?.VoiceDesignSampleText;
        }

        public async Task SetSampleTextAsync(string? sampleText)
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            await MutateSettingsAsync(db, s => s.VoiceDesignSampleText = sampleText);
            await db.SaveChangesAsync();
            NotifyChanged();
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
