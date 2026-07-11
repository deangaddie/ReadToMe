using Microsoft.Extensions.Logging.Abstractions;
using Read2Me.AppData.Entities;
using Read2Me.Services;
using Read2Me.Tests.Infrastructure;
using Xunit;

namespace Read2Me.Tests.Services
{
    public class LlmSettingsServiceTests : AppDbTestBase
    {
        private LlmSettingsService NewService() =>
            new(Factory, NullLogger<LlmSettingsService>.Instance);

        private static LlmServerConfig Config(string name) => new()
        {
            Name = name,
            BaseUrl = "http://localhost:8080",
        };

        [Fact]
        public async Task CreateConfig_First_AutoActivates()
        {
            var svc = NewService();
            var created = await svc.CreateConfigAsync(Config("A"));

            Assert.Equal(created.Id, await svc.GetActiveConfigIdAsync());
        }

        [Fact]
        public async Task CreateConfig_Second_DoesNotChangeActive()
        {
            var svc = NewService();
            var first = await svc.CreateConfigAsync(Config("A"));
            await svc.CreateConfigAsync(Config("B"));

            Assert.Equal(first.Id, await svc.GetActiveConfigIdAsync());
        }

        [Fact]
        public async Task CreateConfig_AssignsFreshId_IgnoringIncomingId()
        {
            var svc = NewService();
            var incoming = Config("A");
            incoming.Id = 999;
            var created = await svc.CreateConfigAsync(incoming);

            Assert.NotEqual(999, created.Id);
        }

        [Fact]
        public async Task GetAllConfigs_OrderedByName()
        {
            var svc = NewService();
            await svc.CreateConfigAsync(Config("Zebra"));
            await svc.CreateConfigAsync(Config("Alpha"));

            var all = await svc.GetAllConfigsAsync();
            Assert.Equal(new[] { "Alpha", "Zebra" }, all.Select(c => c.Name).ToArray());
        }

        [Fact]
        public async Task SetActiveConfig_SwitchesActive()
        {
            var svc = NewService();
            var a = await svc.CreateConfigAsync(Config("A"));
            var b = await svc.CreateConfigAsync(Config("B"));

            await svc.SetActiveConfigAsync(b.Id);
            Assert.Equal(b.Id, await svc.GetActiveConfigIdAsync());
        }

        [Fact]
        public async Task GetActiveConfig_ReturnsFullEntity()
        {
            var svc = NewService();
            var a = await svc.CreateConfigAsync(Config("A"));

            var active = await svc.GetActiveConfigAsync();
            Assert.NotNull(active);
            Assert.Equal(a.Id, active!.Id);
        }

        [Fact]
        public async Task DeleteActiveConfig_WithOneSurvivor_PromotesSurvivor()
        {
            var svc = NewService();
            var a = await svc.CreateConfigAsync(Config("A"));
            var b = await svc.CreateConfigAsync(Config("B"));

            await svc.DeleteConfigAsync(a.Id);

            Assert.Equal(b.Id, await svc.GetActiveConfigIdAsync());
        }

        [Fact]
        public async Task DeleteActiveConfig_WithMultipleSurvivors_ClearsActive()
        {
            var svc = NewService();
            var a = await svc.CreateConfigAsync(Config("A"));
            await svc.CreateConfigAsync(Config("B"));
            await svc.CreateConfigAsync(Config("C"));

            await svc.DeleteConfigAsync(a.Id);

            Assert.Null(await svc.GetActiveConfigIdAsync());
        }

        [Fact]
        public async Task DeleteInactiveConfig_LeavesActiveUntouched()
        {
            var svc = NewService();
            var a = await svc.CreateConfigAsync(Config("A"));
            var b = await svc.CreateConfigAsync(Config("B"));

            await svc.DeleteConfigAsync(b.Id);

            Assert.Equal(a.Id, await svc.GetActiveConfigIdAsync());
        }

        [Fact]
        public async Task DeleteMissingConfig_NoThrow_NoChange()
        {
            var svc = NewService();
            var a = await svc.CreateConfigAsync(Config("A"));

            await svc.DeleteConfigAsync(12345);

            Assert.Equal(a.Id, await svc.GetActiveConfigIdAsync());
            Assert.Single(await svc.GetAllConfigsAsync());
        }

        [Fact]
        public async Task EscalationChainOrder_RoundTrips()
        {
            var svc = NewService();
            var c1 = await svc.CreateConfigAsync(Config("A"));
            var c2 = await svc.CreateConfigAsync(Config("B"));
            var c3 = await svc.CreateConfigAsync(Config("C"));
            var ordered = new[] { c3.Id, c1.Id, c2.Id };

            await svc.SetEscalationConfigIdsAsync(ordered);

            Assert.Equal(ordered, await svc.GetEscalationConfigIdsAsync());
        }

        [Fact]
        public async Task GetEscalationConfigIds_PrunesStaleId_AndReSaves()
        {
            var svc = NewService();
            var c = await svc.CreateConfigAsync(Config("A")); // real id
            await svc.SetEscalationConfigIdsAsync(new[] { c.Id, 999 });

            Assert.Equal(new[] { c.Id }, await svc.GetEscalationConfigIdsAsync());
            // Re-read confirms the prune was persisted, not recomputed each call.
            Assert.Equal(new[] { c.Id }, await NewService().GetEscalationConfigIdsAsync());
        }

        [Fact]
        public async Task DeleteConfig_PrunesFromEscalationChain()
        {
            var svc = NewService();
            var a = await svc.CreateConfigAsync(Config("A"));
            var b = await svc.CreateConfigAsync(Config("B"));
            await svc.SetEscalationConfigIdsAsync(new[] { a.Id, b.Id });

            await svc.DeleteConfigAsync(b.Id);

            Assert.Equal(new[] { a.Id }, await svc.GetEscalationConfigIdsAsync());
        }

        [Fact]
        public async Task SetEscalationConfigIds_FiresOnChanged()
        {
            var svc = NewService();
            int count = 0;
            svc.OnChanged += () => count++;

            await svc.SetEscalationConfigIdsAsync(new[] { 1 });

            Assert.Equal(1, count);
        }

        [Fact]
        public async Task SetSelfConsistency_FiresOnChanged()
        {
            var svc = NewService();
            int count = 0;
            svc.OnChanged += () => count++;

            await svc.SetSelfConsistencyAsync(true);

            Assert.Equal(1, count);
        }

        [Fact]
        public async Task SelfConsistency_RoundTrips_DefaultsFalse()
        {
            var svc = NewService();
            Assert.False(await svc.GetSelfConsistencyAsync());

            await svc.SetSelfConsistencyAsync(true);
            Assert.True(await svc.GetSelfConsistencyAsync());
        }

        [Fact]
        public async Task GetEscalationChain_ResolvesAndDedupes()
        {
            var svc = NewService();
            var a = await svc.CreateConfigAsync(Config("A"));
            var c6 = await svc.CreateConfigAsync(Config("Six"));
            var c7 = await svc.CreateConfigAsync(Config("Seven"));
            await svc.SetActiveConfigAsync(a.Id);
            await svc.SetEscalationConfigIdsAsync(new[] { c6.Id, a.Id, c7.Id });

            var chain = await svc.GetEscalationChainAsync();

            Assert.Equal(new[] { a.Id, c6.Id, c7.Id }, chain.Select(c => c.Id).ToArray());
        }

        [Fact]
        public async Task EmptyEscalation_ChainIsJustActive()
        {
            var svc = NewService();
            var a = await svc.CreateConfigAsync(Config("A"));

            Assert.Empty(await svc.GetEscalationConfigIdsAsync());
            var chain = await svc.GetEscalationChainAsync();
            Assert.Equal(new[] { a.Id }, chain.Select(c => c.Id).ToArray());
        }

        [Fact]
        public async Task OnChanged_FiresForMutations()
        {
            var svc = NewService();
            int count = 0;
            svc.OnChanged += () => count++;

            var a = await svc.CreateConfigAsync(Config("A")); // +1
            await svc.SetActiveConfigAsync(a.Id);             // +1
            await svc.UpdateConfigAsync(a);                   // +1
            await svc.DeleteConfigAsync(a.Id);                // +1

            Assert.Equal(4, count);
        }
    }
}
