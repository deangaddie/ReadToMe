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

        /// <summary>Stores a chain of plain (thinking-off) entries — what these tests exercise.</summary>
        private static Task SetChainAsync(LlmSettingsService svc, IReadOnlyList<int> ids) =>
            svc.SetAttributionChainEntriesAsync(
                ids.Select(id => new AttributionChainEntry(id, Thinking: false)).ToList());

        private static async Task<int[]> ChainIdsAsync(LlmSettingsService svc) =>
            (await svc.GetAttributionChainEntriesAsync()).Select(e => e.ConfigId).ToArray();

        [Fact]
        public async Task Add_AppendsConfig_AndPersistsFlatChain()
        {
            var (svc, p, cfgs) = await SetupAsync(3);
            await SetChainAsync(svc, new[] { cfgs[0].Id });
            await p.LoadAsync();

            await p.AddAsync(cfgs[1].Id);

            // The stored chain IS the flat chain — no active prepend, no tail semantics.
            Assert.Equal(new[] { cfgs[0].Id, cfgs[1].Id }, await ChainIdsAsync(svc));
            Assert.Equal(new[] { cfgs[0].Id, cfgs[1].Id }, p.Chain.Select(c => c.Id).ToArray());
        }

        [Fact]
        public async Task MoveDown_SwapsWithNext_AndPersists()
        {
            var (svc, p, cfgs) = await SetupAsync(4);
            await SetChainAsync(svc, new[] { cfgs[0].Id, cfgs[1].Id, cfgs[2].Id });
            await p.LoadAsync();

            await p.MoveDownAsync(cfgs[0].Id);

            Assert.Equal(new[] { cfgs[1].Id, cfgs[0].Id, cfgs[2].Id },
                await ChainIdsAsync(svc));
            Assert.Equal(new[] { cfgs[1].Id, cfgs[0].Id, cfgs[2].Id },
                p.Chain.Select(c => c.Id).ToArray());
        }

        [Fact]
        public async Task MoveUp_SwapsWithPrevious_AndPersists()
        {
            var (svc, p, cfgs) = await SetupAsync(4);
            await SetChainAsync(svc, new[] { cfgs[0].Id, cfgs[1].Id, cfgs[2].Id });
            await p.LoadAsync();

            await p.MoveUpAsync(cfgs[2].Id);

            Assert.Equal(new[] { cfgs[0].Id, cfgs[2].Id, cfgs[1].Id },
                p.Chain.Select(c => c.Id).ToArray());
        }

        [Fact]
        public async Task Index0_CanMoveDown_AndBeRemoved_LikeAnyOtherRow()
        {
            var (svc, p, cfgs) = await SetupAsync(3);
            await SetChainAsync(svc, new[] { cfgs[0].Id, cfgs[1].Id, cfgs[2].Id });
            await p.LoadAsync();

            // Index 0 is not special: it can move down.
            Assert.True(p.CanMoveDown(cfgs[0].Id));
            Assert.False(p.CanMoveUp(cfgs[0].Id));
            await p.MoveDownAsync(cfgs[0].Id);
            Assert.Equal(new[] { cfgs[1].Id, cfgs[0].Id, cfgs[2].Id },
                p.Chain.Select(c => c.Id).ToArray());

            // And it can be removed — nothing is promoted in its place beyond the natural shift.
            await p.RemoveAsync(cfgs[1].Id);
            Assert.Equal(new[] { cfgs[0].Id, cfgs[2].Id },
                await ChainIdsAsync(svc));
        }

        [Fact]
        public async Task MoveUp_AtFirst_IsNoOp()
        {
            var (svc, p, cfgs) = await SetupAsync(3);
            await SetChainAsync(svc, new[] { cfgs[0].Id, cfgs[1].Id });
            await p.LoadAsync();

            await p.MoveUpAsync(cfgs[0].Id);

            Assert.Equal(new[] { cfgs[0].Id, cfgs[1].Id },
                p.Chain.Select(c => c.Id).ToArray());
        }

        [Fact]
        public async Task MoveDown_AtLast_IsNoOp()
        {
            var (svc, p, cfgs) = await SetupAsync(3);
            await SetChainAsync(svc, new[] { cfgs[0].Id, cfgs[1].Id });
            await p.LoadAsync();

            await p.MoveDownAsync(cfgs[1].Id);

            Assert.Equal(new[] { cfgs[0].Id, cfgs[1].Id },
                p.Chain.Select(c => c.Id).ToArray());
        }

        [Fact]
        public async Task CanMove_ReflectsPositionAcrossWholeChain()
        {
            var (svc, p, cfgs) = await SetupAsync(3);
            await SetChainAsync(svc, new[] { cfgs[0].Id, cfgs[1].Id });
            await p.LoadAsync();

            Assert.False(p.CanMoveUp(cfgs[0].Id));
            Assert.True(p.CanMoveUp(cfgs[1].Id));
            Assert.True(p.CanMoveDown(cfgs[0].Id));
            Assert.False(p.CanMoveDown(cfgs[1].Id));
        }

        [Fact]
        public async Task Remove_DropsConfig_AndPersists()
        {
            var (svc, p, cfgs) = await SetupAsync(3);
            await SetChainAsync(svc, new[] { cfgs[0].Id, cfgs[1].Id });
            await p.LoadAsync();

            await p.RemoveAsync(cfgs[1].Id);

            Assert.Equal(new[] { cfgs[0].Id }, await ChainIdsAsync(svc));
            Assert.Equal(new[] { cfgs[0].Id }, p.Chain.Select(c => c.Id).ToArray());
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
        public async Task AvailableToAdd_ExcludesChainedConfigs()
        {
            var (svc, p, cfgs) = await SetupAsync(4);
            await SetChainAsync(svc, new[] { cfgs[0].Id, cfgs[1].Id });
            await p.LoadAsync();

            // available = all(0,1,2,3) - chained(0,1) = {2,3}
            Assert.Equal(new[] { cfgs[2].Id, cfgs[3].Id },
                p.AvailableToAdd.Select(c => c.Id).OrderBy(x => x).ToArray());
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
            Assert.Equal(new[] { cfgs[1].Id }, p.Chain.Select(c => c.Id).ToArray());
            Assert.Contains(cfgs[0].Id, p.AvailableToAdd.Select(c => c.Id));
        }
    }
}
