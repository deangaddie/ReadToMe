using System.Text.Json;
using Read2Me.App.Shared;
using Read2Me.AppData.Entities;
using Read2Me.Services.Audio.SemanticSimilarity.Settings;
using Xunit;

namespace Read2Me.Tests.App
{
    public class SemanticSimilarityServiceConfigFormTests
    {
        private static SemanticSimilarityServiceConfigForm Valid() => new()
        {
            Name = "MiniLM Local",
            Type = SemanticSimilarityServiceType.MiniLmL6,
            BaseUrl = "http://localhost:8200",
            PassThreshold = 0.85,
        };

        // ---- Validate ----

        [Fact]
        public void Validate_Valid_ReturnsNull()
        {
            Assert.Null(Valid().Validate());
        }

        [Fact]
        public void Validate_BlankName_ReturnsNameError()
        {
            var form = Valid();
            form.Name = "   ";
            Assert.Equal("Name is required.", form.Validate());
        }

        [Fact]
        public void Validate_BlankBaseUrl_ReturnsUrlError()
        {
            var form = Valid();
            form.BaseUrl = "";
            Assert.Equal("Base URL is required.", form.Validate());
        }

        [Theory]
        [InlineData("not-a-url")]
        [InlineData("/relative/path")]
        public void Validate_NonAbsoluteUrl_ReturnsUrlError(string url)
        {
            var form = Valid();
            form.BaseUrl = url;
            Assert.Equal(
                "Base URL must be a valid absolute URL (e.g. http://localhost:8200).",
                form.Validate());
        }

        [Fact]
        public void Validate_ThresholdBelowZero_ReturnsThresholdError()
        {
            var form = Valid();
            form.PassThreshold = -0.01;
            Assert.Equal("Pass threshold must be between 0 and 1 (exclusive).", form.Validate());
        }

        [Fact]
        public void Validate_ThresholdAboveOne_ReturnsThresholdError()
        {
            var form = Valid();
            form.PassThreshold = 1.01;
            Assert.Equal("Pass threshold must be between 0 and 1 (exclusive).", form.Validate());
        }

        // ---- BuildConfig ----

        [Fact]
        public void BuildConfig_SerializesSettingsBlob()
        {
            var config = Valid().BuildConfig();

            Assert.Equal("MiniLM Local", config.Name);
            Assert.Equal(SemanticSimilarityServiceType.MiniLmL6, config.Type);

            var settings = JsonSerializer.Deserialize<SemanticSimilaritySettings>(config.SettingsJson);
            Assert.NotNull(settings);
            Assert.Equal("http://localhost:8200", settings!.BaseUrl);
            Assert.Equal(0.85, settings.PassThreshold);
        }

        [Fact]
        public void BuildConfig_TrimsNameAndUrl()
        {
            var form = Valid();
            form.Name = "  MiniLM  ";
            form.BaseUrl = "  http://localhost:8200  ";

            var config = form.BuildConfig();
            Assert.Equal("MiniLM", config.Name);

            var settings = JsonSerializer.Deserialize<SemanticSimilaritySettings>(config.SettingsJson);
            Assert.Equal("http://localhost:8200", settings!.BaseUrl);
        }

        // ---- FromConfig round-trip ----

        [Fact]
        public void FromConfig_BuildConfig_RoundTrips()
        {
            var original = Valid();
            original.Id = 7;
            var config = original.BuildConfig();

            var round = SemanticSimilarityServiceConfigForm.FromConfig(config);

            Assert.Equal(config.Id, round.Id);
            Assert.Equal(config.Name, round.Name);
            Assert.Equal(config.Type, round.Type);
            Assert.Equal("http://localhost:8200", round.BaseUrl);
            Assert.Equal(0.85, round.PassThreshold);
        }

        [Fact]
        public void FromConfig_EmptySettingsJson_DoesNotThrow()
        {
            var config = new SemanticSimilarityServiceConfig
            {
                Name = "Empty",
                Type = SemanticSimilarityServiceType.MpnetBaseV2,
                SettingsJson = "",
            };

            var form = SemanticSimilarityServiceConfigForm.FromConfig(config);
            Assert.Equal("", form.BaseUrl);
            Assert.Equal(0.85, form.PassThreshold);
        }
    }
}
