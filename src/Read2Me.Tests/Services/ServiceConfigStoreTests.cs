using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Read2Me.AppData.Entities;
using Read2Me.Services;
using Read2Me.Tests.Infrastructure;
using Xunit;

namespace Read2Me.Tests.Services
{
    /// <summary>
    /// Behavioural tests for ServiceConfigStore, exercised via the TranscriptionServiceConfig adapter.
    /// Per-adapter wiring tests live in LlmSettingsServiceTests / TranscriptionSettingsServiceTests.
    /// </summary>
    public class ServiceConfigStoreTests : AppDbTestBase
    {
        private ServiceConfigStore<TranscriptionServiceConfig> NewStore() =>
            new(Factory,
                NullLogger<TranscriptionSettingsService>.Instance,
                db => db.TranscriptionServiceConfigs,
                s => s.ActiveTranscriptionConfigId,
                (s, id) => s.ActiveTranscriptionConfigId = id,
                c => c.Id,
                (c, id) => c.Id = id,
                "Test");

        private static TranscriptionServiceConfig Config(string name) => new()
        {
            Name = name,
            Type = TranscriptionServiceType.LocalWhisper,
            SettingsJson = "{}",
        };

        [Fact]
        public async Task Create_FirstConfig_AutoActivates()
        {
            var store = NewStore();
            var created = await store.CreateConfigAsync(Config("A"));

            Assert.Equal(created.Id, await store.GetActiveConfigIdAsync());
        }

        [Fact]
        public async Task Create_SecondConfig_DoesNotChangeActive()
        {
            var store = NewStore();
            var first = await store.CreateConfigAsync(Config("A"));
            await store.CreateConfigAsync(Config("B"));

            Assert.Equal(first.Id, await store.GetActiveConfigIdAsync());
        }

        [Fact]
        public async Task Delete_ActiveConfig_WithOneRemaining_AutoActivatesRemaining()
        {
            var store = NewStore();
            var a = await store.CreateConfigAsync(Config("A"));
            var b = await store.CreateConfigAsync(Config("B"));

            await store.DeleteConfigAsync(a.Id);

            Assert.Equal(b.Id, await store.GetActiveConfigIdAsync());
        }

        [Fact]
        public async Task Delete_ActiveConfig_WithMultipleRemaining_ClearsActive()
        {
            var store = NewStore();
            var a = await store.CreateConfigAsync(Config("A"));
            await store.CreateConfigAsync(Config("B"));
            await store.CreateConfigAsync(Config("C"));

            await store.DeleteConfigAsync(a.Id);

            Assert.Null(await store.GetActiveConfigIdAsync());
        }

        [Fact]
        public async Task Delete_NonActiveConfig_LeavesActiveUnchanged()
        {
            var store = NewStore();
            var a = await store.CreateConfigAsync(Config("A"));
            var b = await store.CreateConfigAsync(Config("B"));

            await store.DeleteConfigAsync(b.Id);

            Assert.Equal(a.Id, await store.GetActiveConfigIdAsync());
        }

        [Fact]
        public async Task SetActive_PersistsToCorrectAppSettingsField_TranscriptionAdapter()
        {
            var store = NewStore();
            var a = await store.CreateConfigAsync(Config("A"));
            var b = await store.CreateConfigAsync(Config("B"));

            await store.SetActiveConfigAsync(b.Id);

            Assert.Equal(b.Id, await store.GetActiveConfigIdAsync());

            // Verify the Llm field was NOT touched (cross-field isolation)
            var llmStore = new ServiceConfigStore<LlmServerConfig>(
                Factory,
                NullLogger<LlmSettingsService>.Instance,
                db => db.LlmServerConfigs,
                s => s.ActiveLlmConfigId,
                (s, id) => s.ActiveLlmConfigId = id,
                c => c.Id,
                (c, id) => c.Id = id,
                "LLM");
            Assert.Null(await llmStore.GetActiveConfigIdAsync());
        }
    }
}
