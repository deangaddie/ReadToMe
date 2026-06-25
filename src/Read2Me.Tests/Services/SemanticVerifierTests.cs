using Microsoft.Extensions.Logging.Abstractions;
using Read2Me.AppData.Entities;
using Read2Me.Services;
using Read2Me.Services.Audio.SemanticSimilarity;
using Read2Me.Services.Audio.SemanticSimilarity.Settings;
using System.Text.Json;
using Xunit;

namespace Read2Me.Tests.Services
{
    public class SemanticVerifierTests
    {
        private static SemanticSimilarityServiceConfig Config(double threshold = 0.85) => new()
        {
            Id = 1,
            Name = "Test",
            Type = SemanticSimilarityServiceType.MiniLmL6,
            SettingsJson = JsonSerializer.Serialize(new SemanticSimilaritySettings
            {
                BaseUrl = "http://localhost:8200",
                PassThreshold = threshold,
            }),
        };

        private static SemanticVerifier Build(
            double? score,
            SemanticSimilarityServiceConfig? activeConfig,
            bool clientThrows = false)
        {
            var fakeSettings = new FakeSettings(activeConfig);
            var fakeClient = new FakeClient(score, clientThrows);
            var fakeResolver = new FakeResolver(fakeClient);
            return new SemanticVerifier(
                fakeSettings, fakeResolver,
                NullLogger<SemanticVerifier>.Instance);
        }

        [Fact]
        public async Task ScoreAboveThreshold_ReturnsTrue()
        {
            var sut = Build(score: 0.91, activeConfig: Config(0.85));

            var (passes, score, _) = await sut.PassesAsync("original", "transcript");

            Assert.True(passes);
            Assert.Equal(0.91, score);
        }

        [Fact]
        public async Task ScoreEqualToThreshold_ReturnsTrue()
        {
            var sut = Build(score: 0.85, activeConfig: Config(0.85));

            var (passes, _, _) = await sut.PassesAsync("original", "transcript");

            Assert.True(passes);
        }

        [Fact]
        public async Task ScoreBelowThreshold_ReturnsFalse()
        {
            var sut = Build(score: 0.72, activeConfig: Config(0.85));

            var (passes, score, _) = await sut.PassesAsync("original", "transcript");

            Assert.False(passes);
            Assert.Equal(0.72, score);
        }

        [Fact]
        public async Task NoActiveConfig_ReturnsFalse_NoThrow()
        {
            var sut = Build(score: null, activeConfig: null);

            var (passes, score, _) = await sut.PassesAsync("original", "transcript");

            Assert.False(passes);
            Assert.Null(score);
        }

        [Fact]
        public async Task ClientThrows_ReturnsFalse_NoThrow()
        {
            var sut = Build(score: null, activeConfig: Config(), clientThrows: true);

            var (passes, score, _) = await sut.PassesAsync("original", "transcript");

            Assert.False(passes);
            Assert.Null(score);
        }

        // ---- Fakes ----

        private sealed class FakeSettings(SemanticSimilarityServiceConfig? config)
            : SemanticSimilaritySettingsService(null!, NullLogger<SemanticSimilaritySettingsService>.Instance)
        {
            public override Task<SemanticSimilarityServiceConfig?> GetActiveConfigAsync() =>
                Task.FromResult(config);
        }

        private sealed class FakeClient(double? score, bool throws) : ISemanticSimilarityClient
        {
            public Task<double> ComputeAsync(
                SemanticSimilarityServiceConfig config, string text1, string text2, CancellationToken ct = default)
            {
                if (throws)
                    throw new HttpRequestException("service unavailable");
                return Task.FromResult(score!.Value);
            }
        }

        private sealed class FakeResolver(ISemanticSimilarityClient client) : ISemanticSimilarityClientResolver
        {
            public ISemanticSimilarityClient Resolve(SemanticSimilarityServiceType type) => client;
        }
    }
}
