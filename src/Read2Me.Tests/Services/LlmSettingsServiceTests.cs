using Microsoft.EntityFrameworkCore;
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

        private static AttributionChainEntry[] Entries(params int[] ids) =>
            ids.Select(id => new AttributionChainEntry(id, Thinking: false)).ToArray();

        private static int[] IdsOf(IReadOnlyList<AttributionChainEntry> entries) =>
            entries.Select(e => e.ConfigId).ToArray();

        [Fact]
        public async Task AttributionChainOrder_RoundTrips()
        {
            var svc = NewService();
            var c1 = await svc.CreateConfigAsync(Config("A"));
            var c2 = await svc.CreateConfigAsync(Config("B"));
            var c3 = await svc.CreateConfigAsync(Config("C"));
            var ordered = Entries(c3.Id, c1.Id, c2.Id);

            await svc.SetAttributionChainEntriesAsync(ordered);

            Assert.Equal(ordered, await svc.GetAttributionChainEntriesAsync());
        }

        [Fact]
        public async Task AttributionChainEntries_RoundTripThinkingFlags()
        {
            var svc = NewService();
            var a = await svc.CreateConfigAsync(Config("A"));
            var b = await svc.CreateConfigAsync(Config("B"));
            var stored = new[]
            {
                new AttributionChainEntry(a.Id, Thinking: false),
                new AttributionChainEntry(b.Id, Thinking: true),
                new AttributionChainEntry(a.Id, Thinking: true),
            };

            await svc.SetAttributionChainEntriesAsync(stored);

            // A fresh service re-reads the persisted column, not any in-memory state.
            Assert.Equal(stored, await NewService().GetAttributionChainEntriesAsync());
        }

        [Fact]
        public async Task LegacyBareIntChainJson_ReadsAsEntriesWithThinkingOff()
        {
            var svc = NewService();
            var a = await svc.CreateConfigAsync(Config("A"));
            var b = await svc.CreateConfigAsync(Config("B"));
            await WriteRawChainJsonAsync($"[{a.Id},{b.Id}]");

            var entries = await svc.GetAttributionChainEntriesAsync();

            Assert.Equal(new[]
            {
                new AttributionChainEntry(a.Id, Thinking: false),
                new AttributionChainEntry(b.Id, Thinking: false),
            }, entries);
        }

        [Fact]
        public async Task ChainWrite_EmitsObjectShape()
        {
            var svc = NewService();
            var a = await svc.CreateConfigAsync(Config("A"));

            await svc.SetAttributionChainEntriesAsync(new[] { new AttributionChainEntry(a.Id, Thinking: true) });

            Assert.Equal($$"""[{"id":{{a.Id}},"thinking":true}]""", await ReadRawChainJsonAsync());
        }

        [Fact]
        public async Task GetAttributionChainEntries_PrunesStaleId_AndReSaves()
        {
            var svc = NewService();
            var c = await svc.CreateConfigAsync(Config("A")); // real id
            await svc.SetAttributionChainEntriesAsync(Entries(c.Id, 999));

            Assert.Equal(new[] { c.Id }, IdsOf(await svc.GetAttributionChainEntriesAsync()));
            // Re-read confirms the prune was persisted, not recomputed each call.
            Assert.Equal(new[] { c.Id }, IdsOf(await NewService().GetAttributionChainEntriesAsync()));
        }

        [Fact]
        public async Task GetAttributionChainEntries_PrunesStaleId_KeepingThinkingRungs()
        {
            var svc = NewService();
            var a = await svc.CreateConfigAsync(Config("A"));
            await svc.SetAttributionChainEntriesAsync(new[]
            {
                new AttributionChainEntry(a.Id, Thinking: false),
                new AttributionChainEntry(999, Thinking: true),
                new AttributionChainEntry(a.Id, Thinking: true),
            });

            Assert.Equal(new[]
            {
                new AttributionChainEntry(a.Id, Thinking: false),
                new AttributionChainEntry(a.Id, Thinking: true),
            }, await svc.GetAttributionChainEntriesAsync());
        }

        [Fact]
        public async Task DeleteConfig_PrunesFromAttributionChain()
        {
            var svc = NewService();
            var a = await svc.CreateConfigAsync(Config("A"));
            var b = await svc.CreateConfigAsync(Config("B"));
            await svc.SetAttributionChainEntriesAsync(Entries(a.Id, b.Id));

            await svc.DeleteConfigAsync(b.Id);

            Assert.Equal(new[] { a.Id }, IdsOf(await svc.GetAttributionChainEntriesAsync()));
        }

        [Fact]
        public async Task DeleteConfig_PrunesEveryRungOfThatConfig_RegardlessOfThinking()
        {
            var svc = NewService();
            var a = await svc.CreateConfigAsync(Config("A"));
            var b = await svc.CreateConfigAsync(Config("B"));
            await svc.SetAttributionChainEntriesAsync(new[]
            {
                new AttributionChainEntry(a.Id, Thinking: false),
                new AttributionChainEntry(b.Id, Thinking: false),
                new AttributionChainEntry(b.Id, Thinking: true),
            });

            await svc.DeleteConfigAsync(b.Id);

            Assert.Equal(new[] { new AttributionChainEntry(a.Id, Thinking: false) },
                await svc.GetAttributionChainEntriesAsync());
        }

        [Fact]
        public async Task DeleteConfig_AtIndexZero_ShortensChain_NoPromotion()
        {
            var svc = NewService();
            var a = await svc.CreateConfigAsync(Config("A"));
            var b = await svc.CreateConfigAsync(Config("B"));
            await svc.SetAttributionChainEntriesAsync(Entries(a.Id, b.Id));

            await svc.DeleteConfigAsync(a.Id); // index 0

            Assert.Equal(new[] { b.Id }, IdsOf(await svc.GetAttributionChainEntriesAsync()));
        }

        [Fact]
        public async Task SetAttributionChainEntries_FiresOnChanged()
        {
            var svc = NewService();
            int count = 0;
            svc.OnChanged += () => count++;

            await svc.SetAttributionChainEntriesAsync(Entries(1));

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
        public async Task GetAttributionChain_ResolvesStoredList_NoActivePrepend_Dedupes()
        {
            var svc = NewService();
            var a = await svc.CreateConfigAsync(Config("A"));
            var c6 = await svc.CreateConfigAsync(Config("Six"));
            var c7 = await svc.CreateConfigAsync(Config("Seven"));
            await svc.SetActiveConfigAsync(a.Id);
            // Active (a) is NOT prepended; the stored list is returned in order, deduped.
            await svc.SetAttributionChainEntriesAsync(Entries(c6.Id, c7.Id, c6.Id));

            var chain = await svc.GetAttributionChainAsync();

            Assert.Equal(new[] { c6.Id, c7.Id }, chain.Select(s => s.Config.Id).ToArray());
        }

        [Fact]
        public async Task GetAttributionChain_DedupesByPair_KeepingSameConfigAtBothThinkingValues()
        {
            var svc = NewService();
            var a = await svc.CreateConfigAsync(Config("A"));
            await svc.SetAttributionChainEntriesAsync(new[]
            {
                new AttributionChainEntry(a.Id, Thinking: false),
                new AttributionChainEntry(a.Id, Thinking: true),
                new AttributionChainEntry(a.Id, Thinking: false), // exact duplicate — collapses
            });

            var chain = await svc.GetAttributionChainAsync();

            Assert.Equal([(a.Id, false), (a.Id, true)], chain.Select(s => (s.Config.Id, s.Thinking)).ToArray());
        }

        [Fact]
        public async Task EmptyChain_WithActive_FallsBackToActive_ThinkingOff()
        {
            var svc = NewService();
            var a = await svc.CreateConfigAsync(Config("A"));

            Assert.Empty(await svc.GetAttributionChainEntriesAsync());
            var step = Assert.Single(await svc.GetAttributionChainAsync());
            Assert.Equal(a.Id, step.Config.Id);
            Assert.False(step.Thinking);
        }

        [Fact]
        public async Task EmptyChain_NoActive_ResolvesEmpty()
        {
            var svc = NewService();

            var chain = await svc.GetAttributionChainAsync();

            Assert.Empty(chain);
        }

        [Fact]
        public async Task CorruptedChainJson_DegradesToEmptyChain_FallsBackToActive()
        {
            var svc = NewService();
            var a = await svc.CreateConfigAsync(Config("A"));
            await svc.SetActiveConfigAsync(a.Id);
            // Poke a corrupted JSON blob directly into the settings row.
            await WriteRawChainJsonAsync("{ not an array");

            Assert.Empty(await svc.GetAttributionChainEntriesAsync());
            var chain = await svc.GetAttributionChainAsync();
            Assert.Equal(new[] { a.Id }, chain.Select(s => s.Config.Id).ToArray());
        }

        [Theory]
        [InlineData("""["three"]""")]      // array of the wrong element type
        [InlineData("""[{"thinking":true}]""")] // object entry with no id
        [InlineData("""{"id":1}""")]       // object, not an array
        [InlineData("""[{"id":1,"thinking":"yes"}]""")] // thinking present but not a boolean
        public async Task MalformedChainJson_ReadsAsEmptyChain(string json)
        {
            var svc = NewService();
            await svc.CreateConfigAsync(Config("A"));
            await WriteRawChainJsonAsync(json);

            Assert.Empty(await svc.GetAttributionChainEntriesAsync());
        }

        private async Task WriteRawChainJsonAsync(string json)
        {
            await using var db = await Factory.CreateDbContextAsync();
            var settings = await db.Settings.SingleAsync();
            settings.AttributionChainIdsJson = json;
            await db.SaveChangesAsync();
        }

        private async Task<string?> ReadRawChainJsonAsync()
        {
            await using var db = await Factory.CreateDbContextAsync();
            return (await db.Settings.SingleAsync()).AttributionChainIdsJson;
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
