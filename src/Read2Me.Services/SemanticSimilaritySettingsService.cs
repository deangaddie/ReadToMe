using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Read2Me.AppData;
using Read2Me.AppData.Entities;
using Read2Me.Services.Events;

namespace Read2Me.Services
{
    public class SemanticSimilaritySettingsService
    {
        private readonly ServiceConfigStore<SemanticSimilarityServiceConfig> _store;

        public event Action? OnChanged;

        private readonly EventBroadcaster<SettingsChanged>? _changes;

        private void NotifyChanged()
        {
            OnChanged?.Invoke();
            _changes?.Publish(new SettingsChanged(SettingsArea.SemanticSimilarity));
        }

        /// <summary>Without the process-wide change signal (tests, and NSubstitute class proxies, use this arity).</summary>
        public SemanticSimilaritySettingsService(IDbContextFactory<Read2MeDbContext> dbFactory, ILogger<SemanticSimilaritySettingsService> logger)
            : this(dbFactory, logger, null) { }

        public SemanticSimilaritySettingsService(IDbContextFactory<Read2MeDbContext> dbFactory, ILogger<SemanticSimilaritySettingsService> logger,
            EventBroadcaster<SettingsChanged>? changes)
        {
            _changes = changes;
            _store = new ServiceConfigStore<SemanticSimilarityServiceConfig>(
                dbFactory, logger,
                db => db.SemanticSimilarityServiceConfigs,
                s => s.ActiveSemanticConfigId,
                (s, id) => s.ActiveSemanticConfigId = id,
                c => c.Id,
                (c, id) => c.Id = id,
                "SemanticSimilarity");
            _store.OnChanged += () => NotifyChanged();
        }

        public Task<List<SemanticSimilarityServiceConfig>> GetAllConfigsAsync() => _store.GetAllConfigsAsync();
        public Task<int?> GetActiveConfigIdAsync() => _store.GetActiveConfigIdAsync();
        public virtual Task<SemanticSimilarityServiceConfig?> GetActiveConfigAsync() => _store.GetActiveConfigAsync();
        public Task SetActiveConfigAsync(int configId) => _store.SetActiveConfigAsync(configId);
        public Task<SemanticSimilarityServiceConfig> CreateConfigAsync(SemanticSimilarityServiceConfig config) => _store.CreateConfigAsync(config);
        public Task UpdateConfigAsync(SemanticSimilarityServiceConfig config) => _store.UpdateConfigAsync(config);
        public Task DeleteConfigAsync(int configId) => _store.DeleteConfigAsync(configId);
    }
}
