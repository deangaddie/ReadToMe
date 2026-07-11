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
            await svc.SetActiveConfigAsync(cfgs[0].Id); // cfg0 is primary
            var p = new AttributionEscalationPresenter(svc);
            await p.LoadAsync();
            return (svc, p, cfgs);
        }

        [Fact]
        public async Task Add_AppendsConfig_AndPersists()
        {
            var (svc, p, cfgs) = await SetupAsync(3);

            await p.AddAsync(cfgs[1].Id);

            // Whole persisted chain = [primary, ...escalation]; the presenter view is the tail.
            Assert.Equal(new[] { cfgs[0].Id, cfgs[1].Id }, await svc.GetAttributionChainIdsAsync());
            Assert.Equal(new[] { cfgs[1].Id }, p.Escalation.Select(c => c.Id).ToArray());
        }

        [Fact]
        public async Task MoveDown_SwapsWithNext_AndPersists()
        {
            var (svc, p, cfgs) = await SetupAsync(4);
            await svc.SetAttributionChainIdsAsync(new[] { cfgs[1].Id, cfgs[2].Id, cfgs[3].Id });
            await p.LoadAsync();

            await p.MoveDownAsync(cfgs[1].Id);

            Assert.Equal(new[] { cfgs[0].Id, cfgs[2].Id, cfgs[1].Id, cfgs[3].Id },
                await svc.GetAttributionChainIdsAsync());
            Assert.Equal(new[] { cfgs[2].Id, cfgs[1].Id, cfgs[3].Id },
                p.Escalation.Select(c => c.Id).ToArray());
        }

        [Fact]
        public async Task MoveUp_SwapsWithPrevious_AndPersists()
        {
            var (svc, p, cfgs) = await SetupAsync(4);
            await svc.SetAttributionChainIdsAsync(new[] { cfgs[1].Id, cfgs[2].Id, cfgs[3].Id });
            await p.LoadAsync();

            await p.MoveUpAsync(cfgs[3].Id);

            Assert.Equal(new[] { cfgs[1].Id, cfgs[3].Id, cfgs[2].Id },
                p.Escalation.Select(c => c.Id).ToArray());
        }

        [Fact]
        public async Task MoveUp_AtFirst_IsNoOp()
        {
            var (svc, p, cfgs) = await SetupAsync(3);
            await svc.SetAttributionChainIdsAsync(new[] { cfgs[1].Id, cfgs[2].Id });
            await p.LoadAsync();

            await p.MoveUpAsync(cfgs[1].Id);

            Assert.Equal(new[] { cfgs[1].Id, cfgs[2].Id },
                p.Escalation.Select(c => c.Id).ToArray());
        }

        [Fact]
        public async Task MoveDown_AtLast_IsNoOp()
        {
            var (svc, p, cfgs) = await SetupAsync(3);
            await svc.SetAttributionChainIdsAsync(new[] { cfgs[1].Id, cfgs[2].Id });
            await p.LoadAsync();

            await p.MoveDownAsync(cfgs[2].Id);

            Assert.Equal(new[] { cfgs[1].Id, cfgs[2].Id },
                p.Escalation.Select(c => c.Id).ToArray());
        }

        [Fact]
        public async Task CanMoveUp_FalseAtFirst_TrueOtherwise()
        {
            var (svc, p, cfgs) = await SetupAsync(3);
            await svc.SetAttributionChainIdsAsync(new[] { cfgs[1].Id, cfgs[2].Id });
            await p.LoadAsync();

            Assert.False(p.CanMoveUp(cfgs[1].Id));
            Assert.True(p.CanMoveUp(cfgs[2].Id));
            Assert.True(p.CanMoveDown(cfgs[1].Id));
            Assert.False(p.CanMoveDown(cfgs[2].Id));
        }

        [Fact]
        public async Task Remove_DropsConfig_AndPersists()
        {
            var (svc, p, cfgs) = await SetupAsync(3);
            await svc.SetAttributionChainIdsAsync(new[] { cfgs[1].Id, cfgs[2].Id });
            await p.LoadAsync();

            await p.RemoveAsync(cfgs[1].Id);

            Assert.Equal(new[] { cfgs[0].Id, cfgs[2].Id }, await svc.GetAttributionChainIdsAsync());
            Assert.Equal(new[] { cfgs[2].Id }, p.Escalation.Select(c => c.Id).ToArray());
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
        public async Task AvailableToAdd_ExcludesPrimaryAndChained()
        {
            var (svc, p, cfgs) = await SetupAsync(4); // cfg0 primary
            await svc.SetAttributionChainIdsAsync(new[] { cfgs[1].Id });
            await p.LoadAsync();

            // available = all(0,1,2,3) - primary(0) - chained(1) = {2,3}
            Assert.Equal(new[] { cfgs[2].Id, cfgs[3].Id },
                p.AvailableToAdd.Select(c => c.Id).OrderBy(x => x).ToArray());
        }

        [Fact]
        public async Task NoActiveConfig_PrimaryNull()
        {
            var svc = NewSettings();
            var p = new AttributionEscalationPresenter(svc);
            await p.LoadAsync();

            Assert.Null(p.Primary);
        }
    }
}
