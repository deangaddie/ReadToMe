using System.Text.Json;
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
        private readonly IDbContextFactory<Read2MeDbContext> _dbFactory;

        public event Action? OnChanged;

        public LlmSettingsService(IDbContextFactory<Read2MeDbContext> dbFactory, ILogger<LlmSettingsService> logger)
        {
            _dbFactory = dbFactory;
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
        public virtual Task<LlmServerConfig?> GetActiveConfigAsync() => _store.GetActiveConfigAsync();
        public Task SetActiveConfigAsync(int configId) => _store.SetActiveConfigAsync(configId);
        public Task<LlmServerConfig> CreateConfigAsync(LlmServerConfig config) => _store.CreateConfigAsync(config);
        public Task UpdateConfigAsync(LlmServerConfig config) => _store.UpdateConfigAsync(config);

        public async Task DeleteConfigAsync(int configId)
        {
            await _store.DeleteConfigAsync(configId);
            // Eager prune: a deleted config must never linger in the escalation chain.
            var ids = await ReadEscalationIdsAsync();
            if (ids.Contains(configId))
            {
                await WriteEscalationIdsAsync(ids.Where(id => id != configId).ToList());
                OnChanged?.Invoke();
            }
        }

        /// <summary>
        /// Escalation config IDs (the tail tried after the primary), pruned of any ID that no
        /// longer maps to a config. Prunes lazily: if anything was dropped, the column is re-saved.
        /// </summary>
        public virtual async Task<IReadOnlyList<int>> GetEscalationConfigIdsAsync()
        {
            var stored = await ReadEscalationIdsAsync();
            if (stored.Count == 0) return stored;

            await using var db = await _dbFactory.CreateDbContextAsync();
            var existing = await db.LlmServerConfigs.Select(c => c.Id).ToListAsync();
            var existingSet = existing.ToHashSet();

            var pruned = stored.Where(existingSet.Contains).ToList();
            if (pruned.Count != stored.Count)
                await WriteEscalationIdsAsync(pruned);
            return pruned;
        }

        public virtual async Task SetEscalationConfigIdsAsync(IReadOnlyList<int> ids)
        {
            await WriteEscalationIdsAsync(ids);
            OnChanged?.Invoke();
        }

        /// <summary>
        /// Resolved, pruned effective chain: active config first (if any), then escalation
        /// configs in order, deduped by ID. IDs with no matching config are skipped.
        /// </summary>
        public virtual async Task<IReadOnlyList<LlmServerConfig>> GetEscalationChainAsync()
        {
            var escalationIds = await GetEscalationConfigIdsAsync();
            var activeId = await GetActiveConfigIdAsync();

            var orderedIds = new List<int>();
            var seen = new HashSet<int>();
            if (activeId is int aid && seen.Add(aid)) orderedIds.Add(aid);
            foreach (var id in escalationIds)
                if (seen.Add(id)) orderedIds.Add(id);

            if (orderedIds.Count == 0) return Array.Empty<LlmServerConfig>();

            await using var db = await _dbFactory.CreateDbContextAsync();
            var byId = await db.LlmServerConfigs
                .Where(c => orderedIds.Contains(c.Id))
                .ToDictionaryAsync(c => c.Id);

            var chain = new List<LlmServerConfig>();
            foreach (var id in orderedIds)
                if (byId.TryGetValue(id, out var cfg)) chain.Add(cfg);
            return chain;
        }

        public virtual async Task<bool> GetSelfConsistencyAsync()
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var settings = await db.Settings.SingleOrDefaultAsync();
            return settings?.AttributionSelfConsistency ?? false;
        }

        public virtual async Task SetSelfConsistencyAsync(bool value)
        {
            await MutateSettingsAsync(s => s.AttributionSelfConsistency = value);
            OnChanged?.Invoke();
        }

        private async Task<List<int>> ReadEscalationIdsAsync()
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var settings = await db.Settings.SingleOrDefaultAsync();
            return Deserialize(settings?.EscalationConfigIdsJson);
        }

        private async Task WriteEscalationIdsAsync(IReadOnlyList<int> ids)
        {
            await MutateSettingsAsync(s => s.EscalationConfigIdsJson = JsonSerializer.Serialize(ids));
        }

        private static List<int> Deserialize(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return new List<int>();
            try
            {
                return JsonSerializer.Deserialize<List<int>>(json) ?? new List<int>();
            }
            catch (JsonException)
            {
                return new List<int>();
            }
        }

        private async Task MutateSettingsAsync(Action<AppSettings> mutate)
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var settings = await db.Settings.SingleOrDefaultAsync();
            if (settings == null)
            {
                settings = new AppSettings();
                db.Settings.Add(settings);
            }
            mutate(settings);
            await db.SaveChangesAsync();
        }
    }
}
