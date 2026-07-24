using Microsoft.Extensions.Logging.Abstractions;
using Read2Me.App.Shared;
using Read2Me.AppData.Entities;
using Read2Me.Services;
using Read2Me.Tests.Infrastructure;
using Xunit;

namespace Read2Me.Tests.App.LlmSettings
{
    public class AttributionEscalationPresenterTests : AppDbTestBase
    {
        private LlmSettingsService NewSettings() =>
            new(Factory, NullLogger<LlmSettingsService>.Instance);

        private static LlmServerConfig Config(string name) => new()
        {
            Name = name,
            BaseUrl = "http://localhost:8080",
        };

        private async Task<(LlmSettingsService svc, AttributionEscalationPresenter p, LlmServerConfig[] cfgs)> SetupAsync(int count)
        {
            var svc = NewSettings();
            var cfgs = new LlmServerConfig[count];
            for (int i = 0; i < count; i++)
                cfgs[i] = await svc.CreateConfigAsync(Config($"cfg{i}"));
            var p = new AttributionEscalationPresenter(svc);
            await p.LoadAsync();
            return (svc, p, cfgs);
        }

        /// <summary>Stores a chain of plain (thinking-off) entries — what most of these tests exercise.</summary>
        private static Task SetChainAsync(LlmSettingsService svc, IReadOnlyList<int> ids) =>
            svc.SetAttributionChainEntriesAsync(
                ids.Select(id => new AttributionChainEntry(id, Thinking: false)).ToList());

        private static Task SetChainAsync(LlmSettingsService svc, params AttributionChainEntry[] entries) =>
            svc.SetAttributionChainEntriesAsync(entries);

        private static async Task<int[]> ChainIdsAsync(LlmSettingsService svc) =>
            (await svc.GetAttributionChainEntriesAsync()).Select(e => e.ConfigId).ToArray();

        private static async Task<(int Id, bool Thinking)[]> ChainEntriesAsync(LlmSettingsService svc) =>
            (await svc.GetAttributionChainEntriesAsync()).Select(e => (e.ConfigId, e.Thinking)).ToArray();

        private static (int Id, bool Thinking, AttributionPromptStyle Style)[] Options(
            AttributionEscalationPresenter p) =>
            p.AvailableToAdd.Select(o => (o.Config.Id, o.Thinking, o.Style)).ToArray();

        private const AttributionPromptStyle Full = AttributionPromptStyle.Full;
        private const AttributionPromptStyle Simple = AttributionPromptStyle.Simple;

        /// <summary>The four rung variants a single config is offered as, in add-list order.</summary>
        private static (int Id, bool Thinking, AttributionPromptStyle Style)[] AllVariants(int id) =>
            [(id, false, Full), (id, false, Simple), (id, true, Full), (id, true, Simple)];

        private static int[] RowIds(AttributionEscalationPresenter p) =>
            p.Chain.Select(r => r.Config.Id).ToArray();

        [Fact]
        public async Task Add_AppendsConfig_AndPersistsFlatChain()
        {
            var (svc, p, cfgs) = await SetupAsync(3);
            await SetChainAsync(svc, new[] { cfgs[0].Id });
            await p.LoadAsync();

            await p.AddAsync(cfgs[1].Id);

            // The stored chain IS the flat chain — no active prepend, no tail semantics.
            Assert.Equal(new[] { cfgs[0].Id, cfgs[1].Id }, await ChainIdsAsync(svc));
            Assert.Equal(new[] { cfgs[0].Id, cfgs[1].Id }, RowIds(p));
        }

        [Fact]
        public async Task Add_DefaultsToThinkingOff()
        {
            var (svc, p, cfgs) = await SetupAsync(2);

            await p.AddAsync(cfgs[0].Id);

            Assert.Equal(new[] { (cfgs[0].Id, false) }, await ChainEntriesAsync(svc));
            Assert.False(p.Chain[0].Thinking);
        }

        [Fact]
        public async Task Add_WithThinking_AppendsThinkingRung()
        {
            var (svc, p, cfgs) = await SetupAsync(2);

            await p.AddAsync(cfgs[0].Id, thinking: true);

            Assert.Equal(new[] { (cfgs[0].Id, true) }, await ChainEntriesAsync(svc));
            Assert.True(p.Chain[0].Thinking);
        }

        [Fact]
        public async Task Add_BothModesOfSameConfig_AreDistinctRungs()
        {
            var (svc, p, cfgs) = await SetupAsync(1);

            await p.AddAsync(cfgs[0].Id, thinking: false);
            await p.AddAsync(cfgs[0].Id, thinking: true);

            Assert.Equal(new[] { (cfgs[0].Id, false), (cfgs[0].Id, true) }, await ChainEntriesAsync(svc));
        }

        [Fact]
        public async Task Add_SameModeTwice_IsNoOp()
        {
            var (svc, p, cfgs) = await SetupAsync(1);

            await p.AddAsync(cfgs[0].Id, thinking: true);
            await p.AddAsync(cfgs[0].Id, thinking: true);

            Assert.Equal(new[] { (cfgs[0].Id, true) }, await ChainEntriesAsync(svc));
        }

        [Fact]
        public async Task MoveDown_SwapsWithNext_AndPersists()
        {
            var (svc, p, cfgs) = await SetupAsync(4);
            await SetChainAsync(svc, new[] { cfgs[0].Id, cfgs[1].Id, cfgs[2].Id });
            await p.LoadAsync();

            await p.MoveDownAsync(0);

            Assert.Equal(new[] { cfgs[1].Id, cfgs[0].Id, cfgs[2].Id },
                await ChainIdsAsync(svc));
            Assert.Equal(new[] { cfgs[1].Id, cfgs[0].Id, cfgs[2].Id }, RowIds(p));
        }

        [Fact]
        public async Task MoveUp_SwapsWithPrevious_AndPersists()
        {
            var (svc, p, cfgs) = await SetupAsync(4);
            await SetChainAsync(svc, new[] { cfgs[0].Id, cfgs[1].Id, cfgs[2].Id });
            await p.LoadAsync();

            await p.MoveUpAsync(2);

            Assert.Equal(new[] { cfgs[0].Id, cfgs[2].Id, cfgs[1].Id }, RowIds(p));
        }

        [Fact]
        public async Task Index0_CanMoveDown_AndBeRemoved_LikeAnyOtherRow()
        {
            var (svc, p, cfgs) = await SetupAsync(3);
            await SetChainAsync(svc, new[] { cfgs[0].Id, cfgs[1].Id, cfgs[2].Id });
            await p.LoadAsync();

            // Index 0 is not special: it can move down.
            Assert.True(p.CanMoveDown(0));
            Assert.False(p.CanMoveUp(0));
            await p.MoveDownAsync(0);
            Assert.Equal(new[] { cfgs[1].Id, cfgs[0].Id, cfgs[2].Id }, RowIds(p));

            // And it can be removed — nothing is promoted in its place beyond the natural shift.
            await p.RemoveAsync(0);
            Assert.Equal(new[] { cfgs[0].Id, cfgs[2].Id }, await ChainIdsAsync(svc));
        }

        [Fact]
        public async Task MoveUp_AtFirst_IsNoOp()
        {
            var (svc, p, cfgs) = await SetupAsync(3);
            await SetChainAsync(svc, new[] { cfgs[0].Id, cfgs[1].Id });
            await p.LoadAsync();

            await p.MoveUpAsync(0);

            Assert.Equal(new[] { cfgs[0].Id, cfgs[1].Id }, RowIds(p));
        }

        [Fact]
        public async Task MoveDown_AtLast_IsNoOp()
        {
            var (svc, p, cfgs) = await SetupAsync(3);
            await SetChainAsync(svc, new[] { cfgs[0].Id, cfgs[1].Id });
            await p.LoadAsync();

            await p.MoveDownAsync(1);

            Assert.Equal(new[] { cfgs[0].Id, cfgs[1].Id }, RowIds(p));
        }

        [Fact]
        public async Task MoveAndRemove_OutOfRangeIndex_IsNoOp()
        {
            var (svc, p, cfgs) = await SetupAsync(2);
            await SetChainAsync(svc, new[] { cfgs[0].Id, cfgs[1].Id });
            await p.LoadAsync();

            await p.MoveUpAsync(-1);
            await p.MoveDownAsync(7);
            await p.RemoveAsync(7);

            Assert.False(p.CanMoveUp(-1));
            Assert.False(p.CanMoveDown(7));
            Assert.Equal(new[] { (cfgs[0].Id, false), (cfgs[1].Id, false) }, await ChainEntriesAsync(svc));
        }

        [Fact]
        public async Task CanMove_ReflectsPositionAcrossWholeChain()
        {
            var (svc, p, cfgs) = await SetupAsync(3);
            await SetChainAsync(svc, new[] { cfgs[0].Id, cfgs[1].Id });
            await p.LoadAsync();

            Assert.False(p.CanMoveUp(0));
            Assert.True(p.CanMoveUp(1));
            Assert.True(p.CanMoveDown(0));
            Assert.False(p.CanMoveDown(1));
        }

        [Fact]
        public async Task Remove_DropsRow_AndPersists()
        {
            var (svc, p, cfgs) = await SetupAsync(3);
            await SetChainAsync(svc, new[] { cfgs[0].Id, cfgs[1].Id });
            await p.LoadAsync();

            await p.RemoveAsync(1);

            Assert.Equal(new[] { cfgs[0].Id }, await ChainIdsAsync(svc));
            Assert.Equal(new[] { cfgs[0].Id }, RowIds(p));
        }

        [Fact]
        public async Task Move_WithDuplicateConfigs_MovesTheAddressedRowOnly()
        {
            var (svc, p, cfgs) = await SetupAsync(2);
            await SetChainAsync(svc,
                new AttributionChainEntry(cfgs[0].Id, Thinking: false),
                new AttributionChainEntry(cfgs[1].Id, Thinking: false),
                new AttributionChainEntry(cfgs[0].Id, Thinking: true));
            await p.LoadAsync();

            // Move the thinking rung (index 2) up past cfg1 — the fast rung at index 0 stays put.
            await p.MoveUpAsync(2);

            Assert.Equal(
                new[] { (cfgs[0].Id, false), (cfgs[0].Id, true), (cfgs[1].Id, false) },
                await ChainEntriesAsync(svc));
            Assert.Equal(new[] { cfgs[0].Id, cfgs[0].Id, cfgs[1].Id }, RowIds(p));
            Assert.Equal(new[] { false, true, false }, p.Chain.Select(r => r.Thinking).ToArray());
        }

        [Fact]
        public async Task Remove_WithDuplicateConfigs_DropsOnlyTheAddressedRow()
        {
            var (svc, p, cfgs) = await SetupAsync(2);
            await SetChainAsync(svc,
                new AttributionChainEntry(cfgs[0].Id, Thinking: false),
                new AttributionChainEntry(cfgs[0].Id, Thinking: true));
            await p.LoadAsync();

            await p.RemoveAsync(1);

            Assert.Equal(new[] { (cfgs[0].Id, false) }, await ChainEntriesAsync(svc));
        }

        [Fact]
        public async Task Chain_SurfacesEachRungsThinkingFlag()
        {
            var (svc, p, cfgs) = await SetupAsync(2);
            await SetChainAsync(svc,
                new AttributionChainEntry(cfgs[0].Id, Thinking: false),
                new AttributionChainEntry(cfgs[1].Id, Thinking: true));
            await p.LoadAsync();

            Assert.Equal(new[] { false, true }, p.Chain.Select(r => r.Thinking).ToArray());
        }

        [Fact]
        public async Task SetSelfConsistency_Persists()
        {
            var (svc, p, _) = await SetupAsync(1);
            Assert.False(p.SelfConsistency);

            await p.SetSelfConsistencyAsync(true);

            Assert.True(await svc.GetSelfConsistencyAsync());
            Assert.True(p.SelfConsistency);
        }

        [Fact]
        public async Task AvailableToAdd_OffersEveryConfigInEveryVariant()
        {
            var (_, p, cfgs) = await SetupAsync(2);

            Assert.Equal([.. AllVariants(cfgs[0].Id), .. AllVariants(cfgs[1].Id)], Options(p));
        }

        [Fact]
        public async Task AvailableToAdd_ExcludesOnlyTheExactRungAlreadyPresent()
        {
            var (svc, p, cfgs) = await SetupAsync(2);
            await SetChainAsync(svc,
                new AttributionChainEntry(cfgs[0].Id, Thinking: false, Simple),
                new AttributionChainEntry(cfgs[1].Id, Thinking: true, Full));
            await p.LoadAsync();

            // Every other variant of each config stays offered.
            Assert.Equal(
                new[]
                {
                    (cfgs[0].Id, false, Full), (cfgs[0].Id, true, Full), (cfgs[0].Id, true, Simple),
                    (cfgs[1].Id, false, Full), (cfgs[1].Id, false, Simple), (cfgs[1].Id, true, Simple),
                },
                Options(p));
        }

        /// <summary>
        /// A rung stored without a style occupies the slot it actually runs as — its config's own
        /// style — rather than leaving that variant still on offer as an effective duplicate.
        /// </summary>
        [Fact]
        public async Task AvailableToAdd_ExcludesInheritedRungByItsEffectiveStyle()
        {
            var svc = NewSettings();
            var simpleCfg = Config("simple");
            simpleCfg.PromptStyle = Simple;
            var cfg = await svc.CreateConfigAsync(simpleCfg);
            await SetChainAsync(svc, new AttributionChainEntry(cfg.Id, Thinking: false));
            var p = new AttributionEscalationPresenter(svc);
            await p.LoadAsync();

            Assert.Equal(Simple, p.Chain[0].Style);
            Assert.DoesNotContain((cfg.Id, false, Simple), Options(p));
            Assert.Contains((cfg.Id, false, Full), Options(p));
        }

        [Fact]
        public async Task AvailableToAdd_DropsConfigEntirely_WhenEveryVariantPresent()
        {
            var (svc, p, cfgs) = await SetupAsync(2);
            await SetChainAsync(svc,
                new AttributionChainEntry(cfgs[0].Id, Thinking: false, Full),
                new AttributionChainEntry(cfgs[0].Id, Thinking: false, Simple),
                new AttributionChainEntry(cfgs[0].Id, Thinking: true, Full),
                new AttributionChainEntry(cfgs[0].Id, Thinking: true, Simple));
            await p.LoadAsync();

            Assert.Equal(AllVariants(cfgs[1].Id), Options(p));
        }

        [Fact]
        public async Task AvailableToAdd_LabelsAndKeys_DistinguishEveryVariant()
        {
            var (_, p, cfgs) = await SetupAsync(1);
            var id = cfgs[0].Id;

            Assert.Equal(
                new[] { "cfg0", "cfg0 (simple)", "cfg0 (thinking)", "cfg0 (simple, thinking)" },
                p.AvailableToAdd.Select(o => o.Label).ToArray());
            Assert.Equal(
                new[] { $"{id}:f:Full", $"{id}:f:Simple", $"{id}:t:Full", $"{id}:t:Simple" },
                p.AvailableToAdd.Select(o => o.Key).ToArray());
        }

        [Fact]
        public async Task Add_WithStyle_AppendsStyledRung_AndSurfacesIt()
        {
            var (svc, p, cfgs) = await SetupAsync(1);

            await p.AddAsync(cfgs[0].Id, thinking: false, style: Simple);

            Assert.Equal(
                new[] { new AttributionChainEntry(cfgs[0].Id, Thinking: false, Simple) },
                await svc.GetAttributionChainEntriesAsync());
            Assert.Equal(Simple, p.Chain[0].Style);
        }

        [Fact]
        public async Task Add_BothStylesOfSameConfig_AreDistinctRungs()
        {
            var (svc, p, cfgs) = await SetupAsync(1);

            await p.AddAsync(cfgs[0].Id, thinking: false, style: Simple);
            await p.AddAsync(cfgs[0].Id, thinking: false, style: Full);

            Assert.Equal(new[] { Simple, Full }, p.Chain.Select(r => r.Style).ToArray());
        }

        [Fact]
        public async Task Add_SameStyleTwice_IsNoOp()
        {
            var (svc, p, cfgs) = await SetupAsync(1);

            await p.AddAsync(cfgs[0].Id, thinking: false, style: Simple);
            await p.AddAsync(cfgs[0].Id, thinking: false, style: Simple);

            Assert.Single(await svc.GetAttributionChainEntriesAsync());
        }

        [Fact]
        public async Task Add_WithoutStyle_StoresNoStyle_SoTheRungInherits()
        {
            var (svc, p, cfgs) = await SetupAsync(1);

            await p.AddAsync(cfgs[0].Id);

            Assert.Null((await svc.GetAttributionChainEntriesAsync())[0].Style);
            Assert.Equal(Full, p.Chain[0].Style); // the config's own
        }

        [Fact]
        public async Task EmptyChain_SurfacesActiveAsFallback()
        {
            var (svc, p, cfgs) = await SetupAsync(2);
            await svc.SetActiveConfigAsync(cfgs[1].Id);
            await p.LoadAsync();

            Assert.Empty(p.Chain);
            Assert.NotNull(p.FallbackConfig);
            Assert.Equal(cfgs[1].Id, p.FallbackConfig!.Id);
        }

        [Fact]
        public async Task NoActiveConfig_FallbackNull()
        {
            var svc = NewSettings();
            var p = new AttributionEscalationPresenter(svc);
            await p.LoadAsync();

            Assert.Empty(p.Chain);
            Assert.Null(p.FallbackConfig);
        }

        [Fact]
        public async Task ActiveConfig_IsNotAutoAddedToChain()
        {
            var (svc, p, cfgs) = await SetupAsync(2);
            await svc.SetActiveConfigAsync(cfgs[0].Id);
            await SetChainAsync(svc, new[] { cfgs[1].Id });
            await p.LoadAsync();

            // The active config does not auto-appear in the chain (that is the whole point of ticket 01).
            Assert.Equal(new[] { cfgs[1].Id }, RowIds(p));
            Assert.Contains((cfgs[0].Id, false, Full), Options(p));
        }

        /// <summary>
        /// End-to-end guard that a deleted config leaves no row. The service eager-prunes, and the
        /// presenter filters unresolvable ids on top — this pins the observable result of both.
        /// </summary>
        [Fact]
        public async Task DeletedConfig_LeavesNoRow()
        {
            var (svc, p, cfgs) = await SetupAsync(2);
            await SetChainAsync(svc, new[] { cfgs[0].Id, cfgs[1].Id });
            await svc.DeleteConfigAsync(cfgs[1].Id);
            await p.LoadAsync();

            Assert.Equal(new[] { cfgs[0].Id }, RowIds(p));
        }
    }
}
