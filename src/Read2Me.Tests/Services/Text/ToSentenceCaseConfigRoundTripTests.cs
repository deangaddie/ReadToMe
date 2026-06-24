using Microsoft.EntityFrameworkCore;
using Read2Me.AppData.Entities;
using Read2Me.Tests.Infrastructure;
using Xunit;

namespace Read2Me.Tests.Services.Text
{
    public class ToSentenceCaseConfigRoundTripTests : AppDbTestBase
    {
        [Fact]
        public async Task ToSentenceCaseConfig_RoundTrip_PreservesAllFields()
        {
            using var db = Factory.CreateDbContext();

            var config = new ParagraphTtsServiceConfig
            {
                Name = "Test",
                Type = ParagraphTtsServiceType.VoxCpm2,
                SettingsJson = "{}",
                ToSentenceCaseConfig = new ToSentenceCaseConfig
                {
                    ParagraphEnabled = true,
                    WordEnabled = false,
                    WordMinLength = 5,
                }
            };

            db.ParagraphTtsServiceConfigs.Add(config);
            await db.SaveChangesAsync();
            var savedId = config.Id;

            using var db2 = Factory.CreateDbContext();
            var reloaded = await db2.ParagraphTtsServiceConfigs
                .Include(c => c.ToSentenceCaseConfig)
                .SingleAsync(c => c.Id == savedId);

            Assert.NotNull(reloaded.ToSentenceCaseConfig);
            Assert.True(reloaded.ToSentenceCaseConfig.ParagraphEnabled);
            Assert.False(reloaded.ToSentenceCaseConfig.WordEnabled);
            Assert.Equal(5, reloaded.ToSentenceCaseConfig.WordMinLength);
            Assert.Equal(savedId, reloaded.ToSentenceCaseConfig.ParagraphTtsServiceConfigId);
        }

        [Fact]
        public async Task DeleteConfig_CascadesToSentenceCaseConfig()
        {
            using var db = Factory.CreateDbContext();

            var config = new ParagraphTtsServiceConfig
            {
                Name = "Cascade",
                Type = ParagraphTtsServiceType.VoxCpm2,
                SettingsJson = "{}",
                ToSentenceCaseConfig = new ToSentenceCaseConfig
                {
                    ParagraphEnabled = true,
                    WordEnabled = true,
                    WordMinLength = 3,
                }
            };

            db.ParagraphTtsServiceConfigs.Add(config);
            await db.SaveChangesAsync();
            var childId = config.ToSentenceCaseConfig!.Id;

            db.ParagraphTtsServiceConfigs.Remove(config);
            await db.SaveChangesAsync();

            using var db3 = Factory.CreateDbContext();
            var gone = !await db3.ToSentenceCaseConfigs.AnyAsync(s => s.Id == childId);
            Assert.True(gone);
        }
    }
}
