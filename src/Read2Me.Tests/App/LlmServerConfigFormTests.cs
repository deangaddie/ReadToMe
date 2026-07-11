using System.Globalization;
using Read2Me.App.Shared;
using Read2Me.AppData.Entities;
using Xunit;

namespace Read2Me.Tests.App
{
    public class LlmServerConfigFormTests
    {
        private static LlmServerConfigForm Valid() => new()
        {
            Name = "Local",
            BaseUrl = "http://localhost:8080",
        };

        // ---- Prompt style ----

        [Fact]
        public void NewForm_DefaultsToFullPromptStyle()
        {
            Assert.Equal(AttributionPromptStyle.Full, Valid().PromptStyle);
        }

        [Fact]
        public void PromptStyle_SurvivesRoundTrip()
        {
            var config = new LlmServerConfig
            {
                Name = "Small",
                BaseUrl = "http://localhost:8080",
                PromptStyle = AttributionPromptStyle.Simple,
            };

            var rebuilt = LlmServerConfigForm.FromConfig(config).BuildConfig();

            Assert.Equal(AttributionPromptStyle.Simple, rebuilt.PromptStyle);
        }

        // ---- Validate: required fields ----

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
                "Base URL must be a valid absolute URL (e.g. http://localhost:8080).",
                form.Validate());
        }

        [Theory]
        [InlineData("http://localhost:8080")]
        [InlineData("https://api.example.com/v1")]
        public void Validate_AbsoluteUrl_Passes(string url)
        {
            var form = Valid();
            form.BaseUrl = url;
            Assert.Null(form.Validate());
        }

        // ---- Validate: numeric params ----

        [Fact]
        public void Validate_BlankNumerics_AreAllowed()
        {
            Assert.Null(Valid().Validate());
        }

        [Theory]
        [InlineData("abc")]
        [InlineData("1.2.3")]
        public void Validate_NonNumericTemperature_ReturnsError(string text)
        {
            var form = Valid();
            form.Temperature = text;
            Assert.Equal("Temperature must be a number.", form.Validate());
        }

        [Theory]
        [InlineData("1.5")]
        [InlineData("ten")]
        public void Validate_NonIntegerMaxTokens_ReturnsError(string text)
        {
            var form = Valid();
            form.MaxTokens = text;
            Assert.Equal("Max tokens must be a whole number.", form.Validate());
        }

        [Fact]
        public void Validate_ReportsFirstFailingField_InDeclaredOrder()
        {
            var form = Valid();
            form.Temperature = "x";
            form.TopP = "y";
            Assert.Equal("Temperature must be a number.", form.Validate());
        }

        // ---- Validate: attribution batch size ----

        [Theory]
        [InlineData("0")]
        [InlineData("-1")]
        [InlineData("abc")]
        [InlineData("1.5")]
        public void Validate_InvalidBatchSize_ReturnsError(string text)
        {
            var form = Valid();
            form.AttributionBatchSize = text;
            Assert.Equal("Paragraphs per request must be a whole number of 1 or more.", form.Validate());
        }

        [Theory]
        [InlineData("1")]
        [InlineData("5")]
        [InlineData("")]
        [InlineData(null)]
        public void Validate_ValidOrBlankBatchSize_Passes(string? text)
        {
            var form = Valid();
            form.AttributionBatchSize = text;
            Assert.Null(form.Validate());
        }

        [Fact]
        public void BuildConfig_BlankBatchSize_DefaultsToOne()
        {
            Assert.Equal(1, Valid().BuildConfig().AttributionBatchSize);
        }

        [Fact]
        public void BuildConfig_BatchSize_Parses()
        {
            var form = Valid();
            form.AttributionBatchSize = "4";
            Assert.Equal(4, form.BuildConfig().AttributionBatchSize);
        }

        // ---- BuildConfig: trimming + omit semantics ----

        [Fact]
        public void BuildConfig_TrimsTextAndNullsBlankOptionalFields()
        {
            var form = new LlmServerConfigForm
            {
                Id = 7,
                Name = "  Local  ",
                BaseUrl = "  http://localhost:8080/  ",
                ApiKey = "   ",
                Model = "   ",
            };

            var cfg = form.BuildConfig();

            Assert.Equal(7, cfg.Id);
            Assert.Equal("Local", cfg.Name);
            Assert.Equal("http://localhost:8080/", cfg.BaseUrl);
            Assert.Null(cfg.ApiKey);
            Assert.Null(cfg.Model);
        }

        [Fact]
        public void BuildConfig_BlankNumerics_BecomeNull()
        {
            var cfg = Valid().BuildConfig();
            Assert.Null(cfg.Temperature);
            Assert.Null(cfg.TopP);
            Assert.Null(cfg.MaxTokens);
            Assert.Null(cfg.FrequencyPenalty);
            Assert.Null(cfg.PresencePenalty);
        }

        [Fact]
        public void BuildConfig_ParsesNumericsWithInvariantCulture()
        {
            var form = Valid();
            form.Temperature = "0.7";
            form.MaxTokens = "2048";

            var cfg = form.BuildConfig();

            Assert.Equal(0.7, cfg.Temperature);
            Assert.Equal(2048, cfg.MaxTokens);
        }

        [Fact]
        public void BuildConfig_OnCommaDecimalCulture_StillParsesDotDecimal()
        {
            var original = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
                var form = Valid();
                form.Temperature = "0.7";
                Assert.Equal(0.7, form.BuildConfig().Temperature);
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = original;
            }
        }

        // ---- Round-trip ----

        [Fact]
        public void FromConfig_ThenBuildConfig_RoundTrips()
        {
            var original = new LlmServerConfig
            {
                Id = 3,
                Name = "Round",
                ApiType = LlmApiType.OpenAiCompatible,
                BaseUrl = "http://localhost:8080",
                ApiKey = "secret",
                Model = "gemma-4b",
                Temperature = 0.5,
                TopP = 0.9,
                MaxTokens = 1024,
                FrequencyPenalty = 0.1,
                PresencePenalty = 0.2,
                AttributionBatchSize = 3,
            };

            var rebuilt = LlmServerConfigForm.FromConfig(original).BuildConfig();

            Assert.Equal(original.Id, rebuilt.Id);
            Assert.Equal(original.Name, rebuilt.Name);
            Assert.Equal(original.BaseUrl, rebuilt.BaseUrl);
            Assert.Equal(original.ApiKey, rebuilt.ApiKey);
            Assert.Equal(original.Model, rebuilt.Model);
            Assert.Equal(original.Temperature, rebuilt.Temperature);
            Assert.Equal(original.MaxTokens, rebuilt.MaxTokens);
            Assert.Equal(original.PresencePenalty, rebuilt.PresencePenalty);
            Assert.Equal(original.AttributionBatchSize, rebuilt.AttributionBatchSize);
        }
    }
}
