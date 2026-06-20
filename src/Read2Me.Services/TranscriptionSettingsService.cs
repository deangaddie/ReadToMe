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
    /// CRUD + active-selection for transcription service configurations.
    /// </summary>
    public class TranscriptionSettingsService
    {
        private readonly ServiceConfigStore<TranscriptionServiceConfig> _store;

        public event Action? OnChanged;

        public TranscriptionSettingsService(IDbContextFactory<Read2MeDbContext> dbFactory, ILogger<TranscriptionSettingsService> logger)
        {
            _store = new ServiceConfigStore<TranscriptionServiceConfig>(
                dbFactory, logger,
                db => db.TranscriptionServiceConfigs,
                s => s.ActiveTranscriptionConfigId,
                (s, id) => s.ActiveTranscriptionConfigId = id,
                c => c.Id,
                (c, id) => c.Id = id,
                "Transcription");
            _store.OnChanged += () => OnChanged?.Invoke();
        }

        public Task<List<TranscriptionServiceConfig>> GetAllConfigsAsync() => _store.GetAllConfigsAsync();
        public Task<int?> GetActiveConfigIdAsync() => _store.GetActiveConfigIdAsync();
        public virtual Task<TranscriptionServiceConfig?> GetActiveConfigAsync() => _store.GetActiveConfigAsync();
        public Task SetActiveConfigAsync(int configId) => _store.SetActiveConfigAsync(configId);
        public Task<TranscriptionServiceConfig> CreateConfigAsync(TranscriptionServiceConfig config) => _store.CreateConfigAsync(config);
        public Task UpdateConfigAsync(TranscriptionServiceConfig config) => _store.UpdateConfigAsync(config);
        public Task DeleteConfigAsync(int configId) => _store.DeleteConfigAsync(configId);
    }
}
