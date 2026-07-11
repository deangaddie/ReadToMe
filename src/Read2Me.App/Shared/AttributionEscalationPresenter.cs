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
    /// index 0 is reorderable and removable like any other step. The active config is exposed only so
    /// the panel can name what attribution falls back to when the chain is empty.
    /// </summary>
    public sealed class AttributionEscalationPresenter(LlmSettingsService settings)
    {
        /// <summary>The whole attribution chain in order, index 0 first.</summary>
        public IReadOnlyList<LlmServerConfig> Chain { get; private set; } = new List<LlmServerConfig>();

        /// <summary>
        /// The active/default config attribution falls back to when the chain is empty, or null when
        /// none is selected. Not a chain member — surfaced purely so the panel can name the fallback.
        /// </summary>
        public LlmServerConfig? FallbackConfig { get; private set; }

        /// <summary>Configs eligible to add: all configs minus those already in the chain.</summary>
        public IReadOnlyList<LlmServerConfig> AvailableToAdd { get; private set; } = new List<LlmServerConfig>();

        /// <summary>Global self-consistency toggle.</summary>
        public bool SelfConsistency { get; private set; }

        /// <summary>Re-read all state from the settings service.</summary>
        public async Task LoadAsync()
        {
            var all = await settings.GetAllConfigsAsync();
            var byId = all.ToDictionary(c => c.Id);

            var activeId = await settings.GetActiveConfigIdAsync();
            FallbackConfig = activeId is int aid && byId.TryGetValue(aid, out var active) ? active : null;

            var chainIds = await settings.GetAttributionChainIdsAsync();
            Chain = chainIds
                .Where(byId.ContainsKey)
                .Select(id => byId[id])
                .ToList();

            var inChain = new HashSet<int>(Chain.Select(c => c.Id));
            AvailableToAdd = all.Where(c => !inChain.Contains(c.Id)).ToList();

            SelfConsistency = await settings.GetSelfConsistencyAsync();
        }

        /// <summary>Append a config to the chain and persist.</summary>
        public async Task AddAsync(int configId)
        {
            var ids = Chain.Select(c => c.Id).ToList();
            if (ids.Contains(configId)) return;
            ids.Add(configId);
            await settings.SetAttributionChainIdsAsync(ids);
            await LoadAsync();
        }

        /// <summary>True when the config is a chain entry that is not already first.</summary>
        public bool CanMoveUp(int configId)
        {
            int i = IndexOf(configId);
            return i > 0;
        }

        /// <summary>True when the config is a chain entry that is not already last.</summary>
        public bool CanMoveDown(int configId)
        {
            int i = IndexOf(configId);
            return i >= 0 && i < Chain.Count - 1;
        }

        /// <summary>Swap the config with its predecessor and persist. No-op at the first position.</summary>
        public Task MoveUpAsync(int configId) => SwapAsync(configId, -1);

        /// <summary>Swap the config with its successor and persist. No-op at the last position.</summary>
        public Task MoveDownAsync(int configId) => SwapAsync(configId, +1);

        private async Task SwapAsync(int configId, int delta)
        {
            int i = IndexOf(configId);
            int j = i + delta;
            if (i < 0 || j < 0 || j >= Chain.Count) return;

            var ids = Chain.Select(c => c.Id).ToList();
            (ids[i], ids[j]) = (ids[j], ids[i]);
            await settings.SetAttributionChainIdsAsync(ids);
            await LoadAsync();
        }

        /// <summary>Drop a config from the chain and persist. Index 0 is removable like any other row.</summary>
        public async Task RemoveAsync(int configId)
        {
            var ids = Chain.Select(c => c.Id).Where(id => id != configId).ToList();
            await settings.SetAttributionChainIdsAsync(ids);
            await LoadAsync();
        }

        /// <summary>Set the global self-consistency toggle and persist.</summary>
        public async Task SetSelfConsistencyAsync(bool value)
        {
            await settings.SetSelfConsistencyAsync(value);
            await LoadAsync();
        }

        private int IndexOf(int configId)
        {
            for (int i = 0; i < Chain.Count; i++)
                if (Chain[i].Id == configId) return i;
            return -1;
        }
    }
}
