using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Read2Me.AppData.Entities;
using Read2Me.Services;

namespace Read2Me.App.Shared
{
    /// <summary>
    /// UI-agnostic state and behaviour behind <c>AttributionEscalationPanel</c>. Holds the resolved
    /// primary + escalation chain and the self-consistency toggle, and drives every mutation
    /// (add/remove/reorder/toggle) through <see cref="LlmSettingsService"/> with an immediate save
    /// followed by a reload from persisted state. The razor component only maps this to MudBlazor
    /// chrome and marshals <c>StateHasChanged</c>.
    /// </summary>
    public sealed class AttributionEscalationPresenter(LlmSettingsService settings)
    {
        /// <summary>The active config shown as the fixed first chain entry, or null when none is selected.</summary>
        public LlmServerConfig? Primary { get; private set; }

        /// <summary>Escalation configs in order (the tail tried after the primary).</summary>
        public IReadOnlyList<LlmServerConfig> Escalation { get; private set; } = new List<LlmServerConfig>();

        /// <summary>Configs eligible to add: all configs minus the primary and existing escalation entries.</summary>
        public IReadOnlyList<LlmServerConfig> AvailableToAdd { get; private set; } = new List<LlmServerConfig>();

        /// <summary>Global self-consistency toggle.</summary>
        public bool SelfConsistency { get; private set; }

        /// <summary>Re-read all state from the settings service.</summary>
        public async Task LoadAsync()
        {
            var all = await settings.GetAllConfigsAsync();
            var byId = all.ToDictionary(c => c.Id);

            var activeId = await settings.GetActiveConfigIdAsync();
            Primary = activeId is int aid && byId.TryGetValue(aid, out var primary) ? primary : null;

            // Transitional (ticket 01): the stored chain is now the *whole* attribution chain
            // including index 0. This presenter still renders a Primary + Escalation split, so it
            // treats the chain's tail (everything after the active config) as the escalation list.
            // Ticket 02 replaces this with a flat chain.
            var chainIds = await settings.GetAttributionChainIdsAsync();
            Escalation = chainIds
                .Where(id => activeId is not int aid2 || id != aid2)
                .Where(byId.ContainsKey)
                .Select(id => byId[id])
                .ToList();

            var inChain = new HashSet<int>(Escalation.Select(c => c.Id));
            if (Primary is not null) inChain.Add(Primary.Id);
            AvailableToAdd = all.Where(c => !inChain.Contains(c.Id)).ToList();

            SelfConsistency = await settings.GetSelfConsistencyAsync();
        }

        /// <summary>Append a config to the escalation chain and persist.</summary>
        public async Task AddAsync(int configId)
        {
            var ids = Escalation.Select(c => c.Id).ToList();
            if (ids.Contains(configId)) return;
            ids.Add(configId);
            await PersistEscalationAsync(ids);
            await LoadAsync();
        }

        // Transitional: persist the whole chain as [primary?, ...escalationTail] so attribution
        // resolves the same effective chain it did before ticket 01. Ticket 02 makes this flat.
        private Task PersistEscalationAsync(IReadOnlyList<int> escalationIds)
        {
            var chain = new List<int>();
            if (Primary is not null) chain.Add(Primary.Id);
            chain.AddRange(escalationIds);
            return settings.SetAttributionChainIdsAsync(chain);
        }

        /// <summary>True when the config is an escalation entry that is not already first.</summary>
        public bool CanMoveUp(int configId)
        {
            int i = IndexOf(configId);
            return i > 0;
        }

        /// <summary>True when the config is an escalation entry that is not already last.</summary>
        public bool CanMoveDown(int configId)
        {
            int i = IndexOf(configId);
            return i >= 0 && i < Escalation.Count - 1;
        }

        /// <summary>Swap the config with its predecessor and persist. No-op at the first position.</summary>
        public Task MoveUpAsync(int configId) => SwapAsync(configId, -1);

        /// <summary>Swap the config with its successor and persist. No-op at the last position.</summary>
        public Task MoveDownAsync(int configId) => SwapAsync(configId, +1);

        private async Task SwapAsync(int configId, int delta)
        {
            int i = IndexOf(configId);
            int j = i + delta;
            if (i < 0 || j < 0 || j >= Escalation.Count) return;

            var ids = Escalation.Select(c => c.Id).ToList();
            (ids[i], ids[j]) = (ids[j], ids[i]);
            await PersistEscalationAsync(ids);
            await LoadAsync();
        }

        /// <summary>Drop a config from the escalation chain and persist.</summary>
        public async Task RemoveAsync(int configId)
        {
            var ids = Escalation.Select(c => c.Id).Where(id => id != configId).ToList();
            await PersistEscalationAsync(ids);
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
            for (int i = 0; i < Escalation.Count; i++)
                if (Escalation[i].Id == configId) return i;
            return -1;
        }
    }
}
