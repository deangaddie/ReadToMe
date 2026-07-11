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
            // Eager prune: a deleted config must never linger in the attribution chain. This
            // includes index 0 — the chain shortens, nothing is promoted.
            var ids = await ReadChainIdsAsync();
            if (ids.Contains(configId))
            {
                await WriteChainIdsAsync(ids.Where(id => id != configId).ToList());
                OnChanged?.Invoke();
            }
        }

        /// <summary>
        /// The whole attribution chain as stored config IDs, index 0 first, pruned of any ID that
        /// no longer maps to a config. Prunes lazily: if anything was dropped, the column is re-saved.
        /// </summary>
        public virtual async Task<IReadOnlyList<int>> GetAttributionChainIdsAsync()
        {
            var stored = await ReadChainIdsAsync();
            if (stored.Count == 0) return stored;

            await using var db = await _dbFactory.CreateDbContextAsync();
            var existing = await db.LlmServerConfigs.Select(c => c.Id).ToListAsync();
            var existingSet = existing.ToHashSet();

            var pruned = stored.Where(existingSet.Contains).ToList();
            if (pruned.Count != stored.Count)
                await WriteChainIdsAsync(pruned);
            return pruned;
        }

        public virtual async Task SetAttributionChainIdsAsync(IReadOnlyList<int> ids)
        {
            await WriteChainIdsAsync(ids);
            OnChanged?.Invoke();
        }

        /// <summary>
        /// Resolved attribution chain: the stored chain in order, deduped by ID, with **no** active
        /// prepend. Fallback rule: a stored chain resolving to one or more configs is returned as-is;
        /// an empty stored chain with an active config resolves to <c>[active]</c>; otherwise empty.
        /// </summary>
        public virtual async Task<IReadOnlyList<LlmServerConfig>> GetAttributionChainAsync()
        {
            var chainIds = await GetAttributionChainIdsAsync();

            var orderedIds = new List<int>();
            var seen = new HashSet<int>();
            foreach (var id in chainIds)
                if (seen.Add(id)) orderedIds.Add(id);

            // Empty stored chain falls back to the active config as a single step.
            if (orderedIds.Count == 0)
            {
                var activeId = await GetActiveConfigIdAsync();
                if (activeId is int aid) orderedIds.Add(aid);
            }

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

        private async Task<List<int>> ReadChainIdsAsync()
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var settings = await db.Settings.SingleOrDefaultAsync();
            return Deserialize(settings?.AttributionChainIdsJson);
        }

        private async Task WriteChainIdsAsync(IReadOnlyList<int> ids)
        {
            await MutateSettingsAsync(s => s.AttributionChainIdsJson = JsonSerializer.Serialize(ids));
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
