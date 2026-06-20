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
    /// CRUD + active-selection for paragraph-TTS service configurations.
    /// </summary>
    public class ParagraphTtsSettingsService
    {
        private readonly ServiceConfigStore<ParagraphTtsServiceConfig> _store;

        public event Action? OnChanged;

        public ParagraphTtsSettingsService(IDbContextFactory<Read2MeDbContext> dbFactory, ILogger<ParagraphTtsSettingsService> logger)
        {
            _store = new ServiceConfigStore<ParagraphTtsServiceConfig>(
                dbFactory, logger,
                db => db.ParagraphTtsServiceConfigs,
                s => s.ActiveParagraphTtsConfigId,
                (s, id) => s.ActiveParagraphTtsConfigId = id,
                c => c.Id,
                (c, id) => c.Id = id,
                "ParagraphTts");
            _store.OnChanged += () => OnChanged?.Invoke();
        }

        public Task<List<ParagraphTtsServiceConfig>> GetAllConfigsAsync() => _store.GetAllConfigsAsync();
        public Task<int?> GetActiveConfigIdAsync() => _store.GetActiveConfigIdAsync();
        public virtual Task<ParagraphTtsServiceConfig?> GetActiveConfigAsync() => _store.GetActiveConfigAsync();
        public Task SetActiveConfigAsync(int configId) => _store.SetActiveConfigAsync(configId);
        public Task<ParagraphTtsServiceConfig> CreateConfigAsync(ParagraphTtsServiceConfig config) => _store.CreateConfigAsync(config);
        public Task UpdateConfigAsync(ParagraphTtsServiceConfig config) => _store.UpdateConfigAsync(config);
        public Task DeleteConfigAsync(int configId) => _store.DeleteConfigAsync(configId);
    }
}
