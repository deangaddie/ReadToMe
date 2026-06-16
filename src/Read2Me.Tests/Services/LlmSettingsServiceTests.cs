using System.Linq;
using System.Threading.Tasks;
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
