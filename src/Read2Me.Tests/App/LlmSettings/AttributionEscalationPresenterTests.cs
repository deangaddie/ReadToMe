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
        public async Task Add_AppendsWithThinkingOff()
        {
            var (svc, p, cfgs) = await SetupAsync(2);

            await p.AddAsync(cfgs[0].Id);

            Assert.Equal(new[] { (cfgs[0].Id, false) }, await ChainEntriesAsync(svc));
            Assert.False(p.Chain[0].Thinking);
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
            await p.SetThinkingAsync(7, true);

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
        public async Task SetThinking_PersistsAndReloads()
        {
            var (svc, p, cfgs) = await SetupAsync(2);
            await SetChainAsync(svc, new[] { cfgs[0].Id, cfgs[1].Id });
            await p.LoadAsync();

            await p.SetThinkingAsync(1, true);

            Assert.Equal(new[] { (cfgs[0].Id, false), (cfgs[1].Id, true) }, await ChainEntriesAsync(svc));
            Assert.Equal(new[] { false, true }, p.Chain.Select(r => r.Thinking).ToArray());

            await p.SetThinkingAsync(1, false);

            Assert.Equal(new[] { (cfgs[0].Id, false), (cfgs[1].Id, false) }, await ChainEntriesAsync(svc));
        }

        [Fact]
        public async Task SetThinking_Off_CollapsesIntoExistingFastRung()
        {
            var (svc, p, cfgs) = await SetupAsync(1);
            await SetChainAsync(svc,
                new AttributionChainEntry(cfgs[0].Id, Thinking: false),
                new AttributionChainEntry(cfgs[0].Id, Thinking: true));
            await p.LoadAsync();

            // The walk dedupes on (config, thinking), so the duplicate must not survive as a ghost row.
            await p.SetThinkingAsync(1, false);

            Assert.Equal(new[] { (cfgs[0].Id, false) }, await ChainEntriesAsync(svc));
            Assert.Single(p.Chain);
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
        public async Task AvailableToAdd_ExcludesConfigsPresentWithThinkingOff()
        {
            var (svc, p, cfgs) = await SetupAsync(4);
            await SetChainAsync(svc, new[] { cfgs[0].Id, cfgs[1].Id });
            await p.LoadAsync();

            // available = all(0,1,2,3) - present-with-thinking-off(0,1) = {2,3}
            Assert.Equal(new[] { cfgs[2].Id, cfgs[3].Id },
                p.AvailableToAdd.Select(c => c.Id).OrderBy(x => x).ToArray());
        }

        [Fact]
        public async Task AvailableToAdd_StillOffersConfigPresentOnlyAsThinkingRung()
        {
            var (svc, p, cfgs) = await SetupAsync(2);
            await SetChainAsync(svc,
                new AttributionChainEntry(cfgs[0].Id, Thinking: true),
                new AttributionChainEntry(cfgs[1].Id, Thinking: false));
            await p.LoadAsync();

            // cfg0 is only a thinking rung — adding a fast rung for it is still a distinct step.
            Assert.Equal(new[] { cfgs[0].Id }, p.AvailableToAdd.Select(c => c.Id).ToArray());
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
            Assert.Contains(cfgs[0].Id, p.AvailableToAdd.Select(c => c.Id));
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
