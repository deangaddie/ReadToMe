using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Read2Me.AppData.Entities;
using Read2Me.Services;
using Read2Me.Tests.Infrastructure;
using Xunit;

namespace Read2Me.Tests.Services
{
    public class TranscriptionSettingsServiceTests : AppDbTestBase
    {
        private TranscriptionSettingsService NewService() =>
            new(Factory, NullLogger<TranscriptionSettingsService>.Instance);

        private static TranscriptionServiceConfig Config(string name) => new()
        {
            Name = name,
            Type = TranscriptionServiceType.LocalWhisper,
            SettingsJson = "{}",
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
        public async Task SetActiveConfig_UpdatesActiveId()
        {
            var svc = NewService();
            var a = await svc.CreateConfigAsync(Config("A"));
            var b = await svc.CreateConfigAsync(Config("B"));

            await svc.SetActiveConfigAsync(b.Id);

            Assert.Equal(b.Id, await svc.GetActiveConfigIdAsync());
        }

        [Fact]
        public async Task DeleteActiveConfig_OneRemaining_AutoActivatesIt()
        {
            var svc = NewService();
            var a = await svc.CreateConfigAsync(Config("A"));
            var b = await svc.CreateConfigAsync(Config("B"));

            await svc.DeleteConfigAsync(a.Id);

            Assert.Equal(b.Id, await svc.GetActiveConfigIdAsync());
        }

        [Fact]
        public async Task DeleteActiveConfig_MultipleRemaining_ClearsActive()
        {
            var svc = NewService();
            var a = await svc.CreateConfigAsync(Config("A"));
            await svc.CreateConfigAsync(Config("B"));
            await svc.CreateConfigAsync(Config("C"));

            await svc.DeleteConfigAsync(a.Id);

            Assert.Null(await svc.GetActiveConfigIdAsync());
        }

        [Fact]
        public async Task DeleteNonActiveConfig_LeavesActiveUnchanged()
        {
            var svc = NewService();
            var a = await svc.CreateConfigAsync(Config("A"));
            var b = await svc.CreateConfigAsync(Config("B"));

            await svc.DeleteConfigAsync(b.Id);

            Assert.Equal(a.Id, await svc.GetActiveConfigIdAsync());
        }

        [Fact]
        public async Task AnyMutation_RaisesOnChanged()
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
