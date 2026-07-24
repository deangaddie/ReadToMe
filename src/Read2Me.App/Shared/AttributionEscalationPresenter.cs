using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Read2Me.AppData.Entities;
using Read2Me.Services;

namespace Read2Me.App.Shared
{
    /// <summary>
    /// One addable chain step: a config paired with the thinking mode and prompt style it would be
    /// added under. The same config yields four options (full/simple × fast/thinking) and each
    /// disappears from the offer list once that exact rung is in the chain, so both are chosen at add
    /// time rather than toggled afterwards.
    /// </summary>
    public sealed record ChainOption(LlmServerConfig Config, bool Thinking, AttributionPromptStyle Style)
    {
        /// <summary>Stable select key — the config id alone cannot distinguish the variants.</summary>
        public string Key => $"{Config.Id}:{(Thinking ? "t" : "f")}:{Style}";

        /// <summary>
        /// Display label. Only the non-default halves are suffixed, so a plain full-prompt rung reads
        /// as the bare config name; matches how the walk names rungs in escalation reasons and logs.
        /// </summary>
        public string Label
        {
            get
            {
                var suffixes = new List<string>(2);
                if (Style == AttributionPromptStyle.Simple) suffixes.Add("simple");
                if (Thinking) suffixes.Add("thinking");
                return suffixes.Count == 0 ? Config.Name : $"{Config.Name} ({string.Join(", ", suffixes)})";
            }
        }
    }

    /// <summary>
    /// UI-agnostic state and behaviour behind <c>AttributionEscalationPanel</c>. Holds the flat
    /// attribution chain (every step, index 0 first) and the self-consistency toggle, and drives every
    /// mutation (add/remove/reorder) through <see cref="LlmSettingsService"/> with an immediate
    /// save followed by a reload from persisted state. The chain has no special hidden first entry:
    /// index 0 is reorderable and removable like any other step. Rows are addressed by index, not by
    /// config ID, because the same config may appear as several rungs differing only in thinking and
    /// prompt style. Both flags are fixed at add time — the row renders them read-only, and swapping
    /// modes means removing the rung and adding the other variant. The active config is exposed only so the panel
    /// can name what attribution falls back to when the chain is empty.
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
        /// Rungs eligible to add: every config in both modes (fast, then thinking), minus the exact
        /// (config, thinking) pairs already in the chain. The walk dedupes on that pair, so an option
        /// is dropped only once its own variant is present — the other variant stays offered.
        /// </summary>
        public IReadOnlyList<ChainOption> AvailableToAdd { get; private set; } = new List<ChainOption>();

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
            Chain = _entries
                .Select(e => new ResolvedChainStep(byId[e.ConfigId], e.Thinking, EffectiveStyle(e, byId)))
                .ToList();

            // Compared on the *effective* style so a legacy entry (no stored style) occupies the slot
            // it actually runs as, rather than leaving that variant still on offer.
            var present = new HashSet<(int, bool, AttributionPromptStyle)>(
                _entries.Select(e => (e.ConfigId, e.Thinking, EffectiveStyle(e, byId))));
            AvailableToAdd = all
                .SelectMany(c => new[]
                {
                    new ChainOption(c, false, AttributionPromptStyle.Full),
                    new ChainOption(c, false, AttributionPromptStyle.Simple),
                    new ChainOption(c, true, AttributionPromptStyle.Full),
                    new ChainOption(c, true, AttributionPromptStyle.Simple),
                })
                .Where(o => !present.Contains((o.Config.Id, o.Thinking, o.Style)))
                .ToList();

            SelfConsistency = await settings.GetSelfConsistencyAsync();
        }

        /// <summary>
        /// Append a config as a rung in the given thinking mode and prompt style and persist. A null
        /// style stores no style, leaving the rung to inherit the config's own.
        /// </summary>
        public async Task AddAsync(int configId, bool thinking = false, AttributionPromptStyle? style = null)
        {
            if (_entries.Any(e => e.ConfigId == configId && e.Thinking == thinking && e.Style == style)) return;
            var entries = _entries.Append(new AttributionChainEntry(configId, thinking, style)).ToList();
            await SaveAsync(entries);
        }

        /// <summary>The style an entry actually runs as: its own when set, else its config's.</summary>
        private static AttributionPromptStyle EffectiveStyle(
            AttributionChainEntry entry, IReadOnlyDictionary<int, LlmServerConfig> byId) =>
            entry.Style ?? byId[entry.ConfigId].PromptStyle;

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

        /// <summary>Set the global self-consistency toggle and persist.</summary>
        public async Task SetSelfConsistencyAsync(bool value)
        {
            await settings.SetSelfConsistencyAsync(value);
            await LoadAsync();
        }

        /// <summary>
        /// Persist a mutated chain and reload. Collapses exact (config, thinking, style) duplicates
        /// first, keeping the first occurrence: the walk dedupes on that triple anyway, so storing a
        /// duplicate would render a row the chain never runs.
        /// </summary>
        private async Task SaveAsync(IEnumerable<AttributionChainEntry> entries)
        {
            var seen = new HashSet<(int, bool, AttributionPromptStyle?)>();
            var deduped = entries.Where(e => seen.Add((e.ConfigId, e.Thinking, e.Style))).ToList();
            await settings.SetAttributionChainEntriesAsync(deduped);
            await LoadAsync();
        }
    }
}
