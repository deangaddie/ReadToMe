using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Read2Me.AppData.Entities;
using Read2Me.Services.Text;
using Read2Me.Tests.Infrastructure;
using Xunit;

namespace Read2Me.Tests.Services.Text
{
    public class TextProcessingStepCatalogTests : AppDbTestBase
    {
        [Fact]
        public void GetAll_ReturnsEmpty_WhenNoStepsRegistered_AndConfigIdZero()
        {
            var catalog = new TextProcessingStepCatalog([], Factory);
            Assert.Empty(catalog.GetAll(0));
        }

        [Fact]
        public void GetAll_ConfigIdZero_ReturnsOnlyBuiltIns()
        {
            var builtIn = new TextProcessingStepDescriptor("builtin-1", "Built-in", "A built-in step");
            var catalog = new TextProcessingStepCatalog([builtIn], Factory);

            var results = catalog.GetAll(0).ToList();

            Assert.Single(results);
            Assert.Equal("builtin-1", results[0].Id);
        }

        [Fact]
        public async Task GetAll_ReturnsSubstitutionSteps_OrderedByOrder()
        {
            using var db = Factory.CreateDbContext();
            var config = new ParagraphTtsServiceConfig
            {
                Name = "Test",
                Type = ParagraphTtsServiceType.VoxCpm2,
                SettingsJson = "{}",
                SubstitutionSteps =
                [
                    new TextSubstitutionStep { FromText = "St.", ToText = "Street", Order = 1 },
                    new TextSubstitutionStep { FromText = "Dr.", ToText = "Doctor", Order = 0 },
                ]
            };
            db.ParagraphTtsServiceConfigs.Add(config);
            await db.SaveChangesAsync();

            var catalog = new TextProcessingStepCatalog([], Factory);
            var results = catalog.GetAll(config.Id).ToList();

            Assert.Equal(2, results.Count);
            Assert.Contains("Dr.", results[0].DisplayName);
            Assert.Contains("Doctor", results[0].DisplayName);
            Assert.Contains("St.", results[1].DisplayName);
            Assert.Contains("Street", results[1].DisplayName);
        }

        [Fact]
        public async Task GetAll_MergesBuiltInsFirst_ThenSubstitutionSteps()
        {
            using var db = Factory.CreateDbContext();
            var config = new ParagraphTtsServiceConfig
            {
                Name = "Merge",
                Type = ParagraphTtsServiceType.VoxCpm2,
                SettingsJson = "{}",
                SubstitutionSteps =
                [
                    new TextSubstitutionStep { FromText = "a", ToText = "b", Order = 0 },
                ]
            };
            db.ParagraphTtsServiceConfigs.Add(config);
            await db.SaveChangesAsync();

            var builtIn = new TextProcessingStepDescriptor("builtin-1", "Built-in", "desc");
            var catalog = new TextProcessingStepCatalog([builtIn], Factory);

            var results = catalog.GetAll(config.Id).ToList();

            Assert.Equal(2, results.Count);
            Assert.Equal("builtin-1", results[0].Id);
            Assert.Equal(config.SubstitutionSteps[0].Id, results[1].Id);
        }
    }
}
