using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Read2Me.AppData.Entities;
using Read2Me.Services;
using Read2Me.Tests.Infrastructure;
using Xunit;

namespace Read2Me.Tests.Services
{
    public class DefaultServiceConfigSeederTests : AppDbMigrationTestBase
    {
        [Fact]
        public async Task SeedAsync_NewDatabase_ActivatesOneConfigPerInfraContainer()
        {
            await using var db = await OpenDbAsync();

            Assert.True(await DefaultServiceConfigSeeder.SeedAsync(db));

            var settings = await db.Settings.SingleAsync();
            var llm = await db.LlmServerConfigs.FindAsync(settings.ActiveLlmConfigId);
            Assert.Equal("qwen-28b", llm!.Model);
            Assert.Equal(DefaultServiceConfigSeeder.LlamaUrl, llm.BaseUrl);

            var tts = await db.ParagraphTtsServiceConfigs
                .Include(c => c.SubstitutionSteps).Include(c => c.ToSentenceCaseConfig)
                .SingleAsync(c => c.Id == settings.ActiveParagraphTtsConfigId);
            Assert.Equal(ParagraphTtsServiceType.Breeze, tts.Type);
            Assert.Contains(DefaultServiceConfigSeeder.AudioCppUrl, tts.SettingsJson);
            Assert.Equal(4, tts.SubstitutionSteps.Count);
            Assert.Equal(["to-sentence-case", .. tts.SubstitutionSteps.OrderBy(s => s.Order).Select(s => s.Id)],
                tts.EnabledStepIds);
            Assert.NotNull(tts.ToSentenceCaseConfig);

            Assert.Equal(VoiceDesignServiceType.Breeze,
                (await db.VoiceDesignServiceConfigs.FindAsync(settings.ActiveVoiceDesignConfigId))!.Type);
            Assert.NotNull(settings.ActiveTranscriptionConfigId);
            Assert.NotNull(settings.ActiveSemanticConfigId);
        }

        [Fact]
        public async Task SeedAsync_NewDatabase_ChainIsTheGemmaChapterPass()
        {
            await using var db = await OpenDbAsync();
            await DefaultServiceConfigSeeder.SeedAsync(db);

            var settings = await db.Settings.SingleAsync();
            var chain = JsonSerializer.Deserialize<List<AttributionChainEntry>>(settings.AttributionChainIdsJson!)!;
            var entry = Assert.Single(chain);
            Assert.False(entry.Thinking);
            Assert.Equal(AttributionPromptStyle.Chapter, entry.Style);
            var gemma = await db.LlmServerConfigs.FindAsync(entry.ConfigId);
            Assert.Equal("gemma-12b", gemma!.Model);
        }

        [Fact]
        public async Task SeedAsync_AnyConfigAlreadyPresent_LeavesDatabaseAlone()
        {
            await using var db = await OpenDbAsync();
            db.TranscriptionServiceConfigs.Add(new TranscriptionServiceConfig
            {
                Name = "Mine", Type = TranscriptionServiceType.LocalWhisper, SettingsJson = "{}",
            });
            await db.SaveChangesAsync();

            Assert.False(await DefaultServiceConfigSeeder.SeedAsync(db));
            Assert.Empty(await db.LlmServerConfigs.ToListAsync());
            Assert.Empty(await db.ParagraphTtsServiceConfigs.ToListAsync());
        }
    }
}
