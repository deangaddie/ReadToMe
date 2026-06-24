using Microsoft.Extensions.Logging.Abstractions;
using Read2Me.AppData.Entities;
using Read2Me.Services;
using Read2Me.Tests.Infrastructure;
using Xunit;

namespace Read2Me.Tests.Services
{
    public class ParagraphTtsSettingsServiceTests : AppDbTestBase
    {
        private ParagraphTtsSettingsService NewService() =>
            new(Factory, NullLogger<ParagraphTtsSettingsService>.Instance);

        private static ParagraphTtsServiceConfig Config(string name) => new()
        {
            Name = name,
            Type = ParagraphTtsServiceType.VoxCpm2,
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

        [Fact]
        public async Task GetActiveConfig_ReturnsDeserializableConfig()
        {
            var svc = NewService();
            await svc.CreateConfigAsync(new ParagraphTtsServiceConfig
            {
                Name = "VoxCpm2 Local",
                Type = ParagraphTtsServiceType.VoxCpm2,
                SettingsJson = """{"BaseUrl":"http://localhost:8000","MaxLen":2048}""",
            });

            var active = await svc.GetActiveConfigAsync();

            Assert.NotNull(active);
            Assert.Equal(ParagraphTtsServiceType.VoxCpm2, active!.Type);
            Assert.Contains("localhost:8000", active.SettingsJson);
        }

        [Fact]
        public async Task CreateConfig_EmptyEnabledStepIds_RemainsEmpty()
        {
            var svc = NewService();
            await svc.CreateConfigAsync(Config("A"));

            var reloaded = (await svc.GetAllConfigsAsync()).Single();
            Assert.Empty(reloaded.EnabledStepIds);
        }

        [Fact]
        public async Task EnabledStepIds_RoundTrips()
        {
            var svc = NewService();
            var cfg = Config("B");
            cfg.EnabledStepIds = ["a", "b"];
            await svc.CreateConfigAsync(cfg);

            var reloaded = (await svc.GetAllConfigsAsync()).Single();
            Assert.Equal(["a", "b"], reloaded.EnabledStepIds);
        }

        // ---- ToSentenceCaseConfig lifecycle ----

        [Fact]
        public async Task CreateConfig_WithToSentenceCaseConfig_PersistsRow()
        {
            var svc = NewService();
            var cfg = Config("A");
            cfg.ToSentenceCaseConfig = new ToSentenceCaseConfig
            {
                ParagraphEnabled = true,
                WordEnabled = false,
                WordMinLength = 9,
            };
            await svc.CreateConfigAsync(cfg);

            var reloaded = (await svc.GetAllConfigsAsync()).Single();
            Assert.NotNull(reloaded.ToSentenceCaseConfig);
            Assert.True(reloaded.ToSentenceCaseConfig!.ParagraphEnabled);
            Assert.False(reloaded.ToSentenceCaseConfig.WordEnabled);
            Assert.Equal(9, reloaded.ToSentenceCaseConfig.WordMinLength);
        }

        [Fact]
        public async Task UpdateConfig_EnableStep_CreatesRow()
        {
            var svc = NewService();
            var created = await svc.CreateConfigAsync(Config("A"));

            created.EnabledStepIds = ["to-sentence-case"];
            created.ToSentenceCaseConfig = new ToSentenceCaseConfig
            {
                ParagraphEnabled = true,
                WordEnabled = true,
                WordMinLength = 5,
            };
            await svc.UpdateConfigAsync(created);

            var reloaded = (await svc.GetAllConfigsAsync()).Single();
            Assert.NotNull(reloaded.ToSentenceCaseConfig);
            Assert.True(reloaded.ToSentenceCaseConfig!.ParagraphEnabled);
            Assert.Equal(5, reloaded.ToSentenceCaseConfig.WordMinLength);
        }

        [Fact]
        public async Task UpdateConfig_UpdateExistingRow_FieldsPersisted()
        {
            var svc = NewService();
            var cfg = Config("A");
            cfg.ToSentenceCaseConfig = new ToSentenceCaseConfig
            {
                ParagraphEnabled = true,
                WordEnabled = true,
                WordMinLength = 5,
            };
            var created = await svc.CreateConfigAsync(cfg);

            created.ToSentenceCaseConfig = new ToSentenceCaseConfig
            {
                ParagraphEnabled = false,
                WordEnabled = true,
                WordMinLength = 10,
            };
            await svc.UpdateConfigAsync(created);

            var reloaded = (await svc.GetAllConfigsAsync()).Single();
            Assert.NotNull(reloaded.ToSentenceCaseConfig);
            Assert.False(reloaded.ToSentenceCaseConfig!.ParagraphEnabled);
            Assert.Equal(10, reloaded.ToSentenceCaseConfig.WordMinLength);
        }

        [Fact]
        public async Task UpdateConfig_DisableStep_DeletesRow()
        {
            var svc = NewService();
            var cfg = Config("A");
            cfg.ToSentenceCaseConfig = new ToSentenceCaseConfig
            {
                ParagraphEnabled = true,
                WordEnabled = true,
                WordMinLength = 5,
            };
            var created = await svc.CreateConfigAsync(cfg);

            created.ToSentenceCaseConfig = null;
            await svc.UpdateConfigAsync(created);

            var reloaded = (await svc.GetAllConfigsAsync()).Single();
            Assert.Null(reloaded.ToSentenceCaseConfig);
        }
    }
}
