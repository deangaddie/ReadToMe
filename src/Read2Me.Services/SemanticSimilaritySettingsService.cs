using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Read2Me.AppData;
using Read2Me.AppData.Entities;

namespace Read2Me.Services
{
    public class SemanticSimilaritySettingsService
    {
        private readonly ServiceConfigStore<SemanticSimilarityServiceConfig> _store;

        public event Action? OnChanged;

        public SemanticSimilaritySettingsService(IDbContextFactory<Read2MeDbContext> dbFactory, ILogger<SemanticSimilaritySettingsService> logger)
        {
            _store = new ServiceConfigStore<SemanticSimilarityServiceConfig>(
                dbFactory, logger,
                db => db.SemanticSimilarityServiceConfigs,
                s => s.ActiveSemanticConfigId,
                (s, id) => s.ActiveSemanticConfigId = id,
                c => c.Id,
                (c, id) => c.Id = id,
                "SemanticSimilarity");
            _store.OnChanged += () => OnChanged?.Invoke();
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
