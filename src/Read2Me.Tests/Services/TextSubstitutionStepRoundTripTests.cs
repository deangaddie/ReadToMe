using Microsoft.EntityFrameworkCore;
using Read2Me.AppData.Entities;
using Read2Me.Tests.Infrastructure;
using Xunit;

namespace Read2Me.Tests.Services
{
    public class TextSubstitutionStepRoundTripTests : AppDbTestBase
    {
        [Fact]
        public async Task SubstitutionSteps_RoundTrip_OrderedByOrder()
        {
            using var db = Factory.CreateDbContext();

            var config = new ParagraphTtsServiceConfig
            {
                Name = "Test",
                Type = ParagraphTtsServiceType.VoxCpm2,
                SettingsJson = "{}",
                SubstitutionSteps =
                [
                    new TextSubstitutionStep { FromText = "Dr.", ToText = "Doctor", Order = 0 },
                    new TextSubstitutionStep { FromText = "St.", ToText = "Street", Order = 1 },
                ]
            };

            db.ParagraphTtsServiceConfigs.Add(config);
            await db.SaveChangesAsync();
            var savedId = config.Id;

            using var db2 = Factory.CreateDbContext();
            var reloaded = await db2.ParagraphTtsServiceConfigs
                .Include(c => c.SubstitutionSteps.OrderBy(s => s.Order))
                .SingleAsync(c => c.Id == savedId);

            Assert.Equal(2, reloaded.SubstitutionSteps.Count);
            Assert.Equal("Dr.", reloaded.SubstitutionSteps[0].FromText);
            Assert.Equal("Doctor", reloaded.SubstitutionSteps[0].ToText);
            Assert.Equal(0, reloaded.SubstitutionSteps[0].Order);
            Assert.Equal("St.", reloaded.SubstitutionSteps[1].FromText);
            Assert.Equal("Street", reloaded.SubstitutionSteps[1].ToText);
            Assert.Equal(1, reloaded.SubstitutionSteps[1].Order);
        }

        [Fact]
        public async Task SubstitutionStep_EmptyToText_IsValid()
        {
            using var db = Factory.CreateDbContext();

            var config = new ParagraphTtsServiceConfig
            {
                Name = "Delete",
                Type = ParagraphTtsServiceType.VoxCpm2,
                SettingsJson = "{}",
                SubstitutionSteps =
                [
                    new TextSubstitutionStep { FromText = "unwanted", ToText = "", Order = 0 },
                ]
            };

            db.ParagraphTtsServiceConfigs.Add(config);
            await db.SaveChangesAsync();
            var savedId = config.Id;

            using var db2 = Factory.CreateDbContext();
            var reloaded = await db2.ParagraphTtsServiceConfigs
                .Include(c => c.SubstitutionSteps)
                .SingleAsync(c => c.Id == savedId);

            Assert.Equal("", reloaded.SubstitutionSteps[0].ToText);
        }

        [Fact]
        public async Task DeleteConfig_CascadesSteps()
        {
            using var db = Factory.CreateDbContext();

            var config = new ParagraphTtsServiceConfig
            {
                Name = "Cascade",
                Type = ParagraphTtsServiceType.VoxCpm2,
                SettingsJson = "{}",
                SubstitutionSteps =
                [
                    new TextSubstitutionStep { FromText = "a", ToText = "b", Order = 0 },
                ]
            };

            db.ParagraphTtsServiceConfigs.Add(config);
            await db.SaveChangesAsync();
            var stepId = config.SubstitutionSteps[0].Id;

            db.ParagraphTtsServiceConfigs.Remove(config);
            await db.SaveChangesAsync();

            using var db3 = Factory.CreateDbContext();
            var stepGone = !await db3.TextSubstitutionSteps.AnyAsync(s => s.Id == stepId);
            Assert.True(stepGone);
        }
    }
}
