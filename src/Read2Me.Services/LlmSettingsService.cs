using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Read2Me.AppData;
using Read2Me.AppData.Entities;

namespace Read2Me.Services
{
    /// <summary>
    /// CRUD + active-selection for LLM server configurations.
    /// </summary>
    public class LlmSettingsService
    {
        private readonly ServiceConfigStore<LlmServerConfig> _store;

        public event Action? OnChanged;

        public LlmSettingsService(IDbContextFactory<Read2MeDbContext> dbFactory, ILogger<LlmSettingsService> logger)
        {
            _store = new ServiceConfigStore<LlmServerConfig>(
                dbFactory, logger,
                db => db.LlmServerConfigs,
                s => s.ActiveLlmConfigId,
                (s, id) => s.ActiveLlmConfigId = id,
                c => c.Id,
                (c, id) => c.Id = id,
                "LLM");
            _store.OnChanged += () => OnChanged?.Invoke();
        }

        public Task<List<LlmServerConfig>> GetAllConfigsAsync() => _store.GetAllConfigsAsync();
        public Task<int?> GetActiveConfigIdAsync() => _store.GetActiveConfigIdAsync();
        public Task<LlmServerConfig?> GetActiveConfigAsync() => _store.GetActiveConfigAsync();
        public Task SetActiveConfigAsync(int configId) => _store.SetActiveConfigAsync(configId);
        public Task<LlmServerConfig> CreateConfigAsync(LlmServerConfig config) => _store.CreateConfigAsync(config);
        public Task UpdateConfigAsync(LlmServerConfig config) => _store.UpdateConfigAsync(config);
        public Task DeleteConfigAsync(int configId) => _store.DeleteConfigAsync(configId);
    }
}
