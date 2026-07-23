using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Read2Me.AppData.Entities;
using Read2Me.Services;

namespace Read2Me.App.Shared
{
    /// <summary>
    /// UI-agnostic state and behaviour behind <c>AttributionEscalationPanel</c>. Holds the flat
    /// attribution chain (every step, index 0 first) and the self-consistency toggle, and drives every
    /// mutation (add/remove/reorder/toggle) through <see cref="LlmSettingsService"/> with an immediate
    /// save followed by a reload from persisted state. The chain has no special hidden first entry:
    /// index 0 is reorderable and removable like any other step. Rows are addressed by index, not by
    /// config ID, because the same config may appear twice (a fast rung and a thinking rung). The
    /// active config is exposed only so the panel can name what attribution falls back to when the
    /// chain is empty.
    /// </summary>
    public sealed class AttributionEscalationPresenter(LlmSettingsService settings)
    {
        /// <summary>The whole attribution chain in order, index 0 first, with each rung's thinking flag.</summary>
        public IReadOnlyList<ResolvedChainStep> Chain { get; private set; } = new List<ResolvedChainStep>();

        /// <summary>
        /// The active/default config attribution falls back to when the chain is empty, or null when
        /// none is selected. Not a chain member — surfaced purely so the panel can name the fallback.
        /// </summary>
        public LlmServerConfig? FallbackConfig { get; private set; }

        /// <summary>
        /// Configs eligible to add: all configs minus those already present as a thinking-off rung.
        /// Adding always appends thinking-off, so an exact duplicate would collapse on the (config,
        /// thinking) dedupe; a config present only as a thinking rung is still worth offering.
        /// </summary>
        public IReadOnlyList<LlmServerConfig> AvailableToAdd { get; private set; } = new List<LlmServerConfig>();

        /// <summary>Global self-consistency toggle.</summary>
        public bool SelfConsistency { get; private set; }

        /// <summary>
        /// The stored chain behind <see cref="Chain"/>, filtered to entries whose config resolves so it
        /// stays index-aligned with the rows the panel renders.
        /// </summary>
        private List<AttributionChainEntry> _entries = new();

        /// <summary>Re-read all state from the settings service.</summary>
        public async Task LoadAsync()
        {
            var all = await settings.GetAllConfigsAsync();
            var byId = all.ToDictionary(c => c.Id);

            var activeId = await settings.GetActiveConfigIdAsync();
            FallbackConfig = activeId is int aid && byId.TryGetValue(aid, out var active) ? active : null;

            _entries = (await settings.GetAttributionChainEntriesAsync())
                .Where(e => byId.ContainsKey(e.ConfigId))
                .ToList();
            Chain = _entries.Select(e => new ResolvedChainStep(byId[e.ConfigId], e.Thinking)).ToList();

            var fastRungs = new HashSet<int>(_entries.Where(e => !e.Thinking).Select(e => e.ConfigId));
            AvailableToAdd = all.Where(c => !fastRungs.Contains(c.Id)).ToList();

            SelfConsistency = await settings.GetSelfConsistencyAsync();
        }

        /// <summary>Append a config as a thinking-off rung and persist.</summary>
        public async Task AddAsync(int configId)
        {
            if (_entries.Any(e => e.ConfigId == configId && !e.Thinking)) return;
            var entries = _entries.Append(new AttributionChainEntry(configId, Thinking: false)).ToList();
            await SaveAsync(entries);
        }

        /// <summary>True when the row exists and is not already first.</summary>
        public bool CanMoveUp(int index) => index > 0 && index < Chain.Count;

        /// <summary>True when the row exists and is not already last.</summary>
        public bool CanMoveDown(int index) => index >= 0 && index < Chain.Count - 1;

        /// <summary>Swap the row with its predecessor and persist. No-op at the first position.</summary>
        public Task MoveUpAsync(int index) => SwapAsync(index, -1);

        /// <summary>Swap the row with its successor and persist. No-op at the last position.</summary>
        public Task MoveDownAsync(int index) => SwapAsync(index, +1);

        private async Task SwapAsync(int index, int delta)
        {
            int j = index + delta;
            if (index < 0 || index >= Chain.Count || j < 0 || j >= _entries.Count) return;

            var entries = _entries.ToList();
            (entries[index], entries[j]) = (entries[j], entries[index]);
            await SaveAsync(entries);
        }

        /// <summary>Drop a row from the chain and persist. Index 0 is removable like any other row.</summary>
        public async Task RemoveAsync(int index)
        {
            if (index < 0 || index >= Chain.Count) return;
            var entries = _entries.ToList();
            entries.RemoveAt(index);
            await SaveAsync(entries);
        }

        /// <summary>Set one rung's thinking flag and persist.</summary>
        public async Task SetThinkingAsync(int index, bool value)
        {
            if (index < 0 || index >= Chain.Count) return;
            var entries = _entries.ToList();
            entries[index] = entries[index] with { Thinking = value };
            await SaveAsync(entries);
        }

        /// <summary>Set the global self-consistency toggle and persist.</summary>
        public async Task SetSelfConsistencyAsync(bool value)
        {
            await settings.SetSelfConsistencyAsync(value);
            await LoadAsync();
        }

        /// <summary>
        /// Persist a mutated chain and reload. Collapses exact (config, thinking) duplicates first,
        /// keeping the first occurrence: the walk dedupes on that pair anyway, so storing a duplicate
        /// would render a row the chain never runs. Toggling thinking off on a rung whose fast twin
        /// already exists therefore drops the rung rather than ghosting it.
        /// </summary>
        private async Task SaveAsync(IEnumerable<AttributionChainEntry> entries)
        {
            var seen = new HashSet<(int, bool)>();
            var deduped = entries.Where(e => seen.Add((e.ConfigId, e.Thinking))).ToList();
            await settings.SetAttributionChainEntriesAsync(deduped);
            await LoadAsync();
        }
    }
}
