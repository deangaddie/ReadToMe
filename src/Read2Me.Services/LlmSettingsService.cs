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
            var entries = await ReadChainEntriesAsync();
            if (entries.Any(e => e.ConfigId == configId))
            {
                await WriteChainEntriesAsync(entries.Where(e => e.ConfigId != configId).ToList());
                OnChanged?.Invoke();
            }
        }

        /// <summary>
        /// The whole attribution chain as stored entries, index 0 first, pruned of any entry whose
        /// config ID no longer maps to a config. Prunes lazily: if anything was dropped, the column is
        /// re-saved.
        /// </summary>
        public virtual async Task<IReadOnlyList<AttributionChainEntry>> GetAttributionChainEntriesAsync()
        {
            var stored = await ReadChainEntriesAsync();
            if (stored.Count == 0) return stored;

            await using var db = await _dbFactory.CreateDbContextAsync();
            var existing = await db.LlmServerConfigs.Select(c => c.Id).ToListAsync();
            var existingSet = existing.ToHashSet();

            var pruned = stored.Where(e => existingSet.Contains(e.ConfigId)).ToList();
            if (pruned.Count != stored.Count)
                await WriteChainEntriesAsync(pruned);
            return pruned;
        }

        public virtual async Task SetAttributionChainEntriesAsync(IReadOnlyList<AttributionChainEntry> entries)
        {
            await WriteChainEntriesAsync(entries);
            OnChanged?.Invoke();
        }

        /// <summary>
        /// Resolved attribution chain: the stored chain in order, deduped by (config ID, thinking,
        /// style) triple, with **no** active prepend. Fallback rule: a stored chain resolving to one
        /// or more configs is returned as-is; an empty stored chain with an active config resolves to
        /// <c>[(active, thinking: false, its own style)]</c>; otherwise empty.
        /// </summary>
        public virtual async Task<IReadOnlyList<ResolvedChainStep>> GetAttributionChainAsync()
        {
            var stored = await GetAttributionChainEntriesAsync();

            var ordered = new List<AttributionChainEntry>();
            var seen = new HashSet<(int, bool, AttributionPromptStyle?)>();
            foreach (var entry in stored)
                if (seen.Add((entry.ConfigId, entry.Thinking, entry.Style))) ordered.Add(entry);

            // Empty stored chain falls back to the active config as a single non-thinking step
            // inheriting that config's own prompt style.
            if (ordered.Count == 0)
            {
                var activeId = await GetActiveConfigIdAsync();
                if (activeId is int aid) ordered.Add(new AttributionChainEntry(aid, Thinking: false));
            }

            if (ordered.Count == 0) return Array.Empty<ResolvedChainStep>();

            var orderedIds = ordered.Select(e => e.ConfigId).Distinct().ToList();

            await using var db = await _dbFactory.CreateDbContextAsync();
            var byId = await db.LlmServerConfigs
                .Where(c => orderedIds.Contains(c.Id))
                .ToDictionaryAsync(c => c.Id);

            var chain = new List<ResolvedChainStep>();
            foreach (var entry in ordered)
                if (byId.TryGetValue(entry.ConfigId, out var cfg))
                    chain.Add(new ResolvedChainStep(cfg, entry.Thinking, entry.Style ?? cfg.PromptStyle));
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

        private async Task<List<AttributionChainEntry>> ReadChainEntriesAsync()
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var settings = await db.Settings.SingleOrDefaultAsync();
            return Deserialize(settings?.AttributionChainIdsJson);
        }

        private async Task WriteChainEntriesAsync(IReadOnlyList<AttributionChainEntry> entries)
        {
            await MutateSettingsAsync(s => s.AttributionChainIdsJson = JsonSerializer.Serialize(entries));
        }

        /// <summary>
        /// Tolerant read of the chain column: the object list written today
        /// (<c>[{"id":3,"thinking":true,"style":"Simple"}]</c>), or the legacy bare-int list
        /// (<c>[3,5]</c>) which maps to entries with thinking off. Both <c>thinking</c> and
        /// <c>style</c> are optional. Anything malformed degrades to an empty chain.
        /// </summary>
        private static List<AttributionChainEntry> Deserialize(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return new List<AttributionChainEntry>();
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind != JsonValueKind.Array) return new List<AttributionChainEntry>();

                var entries = new List<AttributionChainEntry>();
                foreach (var element in doc.RootElement.EnumerateArray())
                {
                    switch (element.ValueKind)
                    {
                        case JsonValueKind.Number when element.TryGetInt32(out var legacyId):
                            entries.Add(new AttributionChainEntry(legacyId, Thinking: false));
                            break;
                        case JsonValueKind.Object when element.TryGetProperty("id", out var idProp)
                                                       && idProp.TryGetInt32(out var id)
                                                       && TryReadThinking(element, out var thinking)
                                                       && TryReadStyle(element, out var style):
                            entries.Add(new AttributionChainEntry(id, thinking, style));
                            break;
                        default:
                            return new List<AttributionChainEntry>();
                    }
                }
                return entries;
            }
            catch (JsonException)
            {
                return new List<AttributionChainEntry>();
            }
        }

        /// <summary>
        /// An absent thinking flag reads as off; a present one must be a real boolean — anything else
        /// is malformed, not a silent "off".
        /// </summary>
        private static bool TryReadThinking(JsonElement entry, out bool thinking)
        {
            if (!entry.TryGetProperty("thinking", out var value))
            {
                thinking = false;
                return true;
            }
            thinking = value.ValueKind == JsonValueKind.True;
            return value.ValueKind is JsonValueKind.True or JsonValueKind.False;
        }

        /// <summary>
        /// An absent (or explicitly null) style flag reads as "inherit the config's own"; a present
        /// one must name a real <see cref="AttributionPromptStyle"/> — anything else is malformed,
        /// not a silent inherit, so a hand-edited typo cannot quietly restore the config default.
        /// </summary>
        private static bool TryReadStyle(JsonElement entry, out AttributionPromptStyle? style)
        {
            style = null;
            if (!entry.TryGetProperty("style", out var value) || value.ValueKind == JsonValueKind.Null)
                return true;
            if (value.ValueKind != JsonValueKind.String)
                return false;
            if (!Enum.TryParse<AttributionPromptStyle>(value.GetString(), ignoreCase: true, out var parsed))
                return false;
            style = parsed;
            return true;
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
