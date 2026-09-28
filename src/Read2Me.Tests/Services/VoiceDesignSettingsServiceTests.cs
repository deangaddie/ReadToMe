using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Read2Me.AppData;
using Read2Me.Services;
using Read2Me.Services.Llm;
using Read2Me.Tests.Infrastructure;
using Xunit;

namespace Read2Me.Tests.Services
{
    /// <summary>
    /// The voice-design sample text is capped so a designed voice's take lands within the Reference
    /// Limit by construction: every generated voice speaks it.
    /// </summary>
    public class VoiceDesignSettingsServiceTests : ProjectDbTestBase
    {
        private async Task<VoiceDesignSettingsService> SutAsync()
        {
            var options = new DbContextOptionsBuilder<Read2MeDbContext>()
                .UseSqlite($"Data Source={Path.Combine(TempDir, "app.db")};Pooling=false")
                .Options;
            var factory = new Factory(options);
            await using (var db = factory.CreateDbContext())
                await db.Database.EnsureCreatedAsync();
            return new VoiceDesignSettingsService(factory, NullLogger<VoiceDesignSettingsService>.Instance);
        }

        [Fact]
        public async Task SampleText_UpTo300Characters_IsStored()
        {
            var sut = await SutAsync();
            var text = new string('a', 300);

            await sut.SetSampleTextAsync(text);

            Assert.Equal(text, await sut.GetSampleTextAsync());
        }

        [Fact]
        public async Task SampleText_Over300Characters_IsRefused_AndTheStoredTextIsKept()
        {
            var sut = await SutAsync();
            await sut.SetSampleTextAsync("A short sentence.");

            var ex = await Assert.ThrowsAsync<ArgumentException>(() => sut.SetSampleTextAsync(new string('a', 301)));

            Assert.Equal("The sample text is 301 characters; it must be 300 or fewer.", ex.Message);
            Assert.Equal("A short sentence.", await sut.GetSampleTextAsync());
        }

        [Fact]
        public void TheBuiltInSampleText_IsWithinTheCap() =>
            Assert.Null(VoiceDesignSettingsService.ValidateSampleText(PromptTemplates.VoiceDesignSampleSentence));

        [Fact]
        public async Task SampleText_Cleared_IsStoredAsNone()
        {
            var sut = await SutAsync();
            await sut.SetSampleTextAsync("A short sentence.");

            await sut.SetSampleTextAsync(null);

            Assert.Null(await sut.GetSampleTextAsync());
        }

        private sealed class Factory(DbContextOptions<Read2MeDbContext> options) : IDbContextFactory<Read2MeDbContext>
        {
            public Read2MeDbContext CreateDbContext() => new(options);
        }
    }
}
