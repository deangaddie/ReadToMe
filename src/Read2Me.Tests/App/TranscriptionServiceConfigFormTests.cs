using System.Text.Json;
using Read2Me.App.Shared;
using Read2Me.AppData.Entities;
using Read2Me.Services.Audio.Transcription.Settings;
using Xunit;

namespace Read2Me.Tests.App
{
    public class TranscriptionServiceConfigFormTests
    {
        private static TranscriptionServiceConfigForm Valid() => new()
        {
            Name = "Local Whisper",
            Type = TranscriptionServiceType.LocalWhisper,
            BaseUrl = "http://localhost:9000",
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
        public void Validate_LocalWhisper_BlankBaseUrl_ReturnsUrlError()
        {
            var form = Valid();
            form.BaseUrl = "";
            Assert.Equal("Base URL is required.", form.Validate());
        }

        [Theory]
        [InlineData("not-a-url")]
        [InlineData("/relative/path")]
        public void Validate_LocalWhisper_NonAbsoluteUrl_ReturnsUrlError(string url)
        {
            var form = Valid();
            form.BaseUrl = url;
            Assert.Equal(
                "Base URL must be a valid absolute URL (e.g. http://localhost:9000).",
                form.Validate());
        }

        // ---- BuildConfig ----

        [Fact]
        public void BuildConfig_SerializesBaseUrlIntoSettingsBlob()
        {
            var config = Valid().BuildConfig();

            Assert.Equal("Local Whisper", config.Name);
            Assert.Equal(TranscriptionServiceType.LocalWhisper, config.Type);

            var settings = JsonSerializer.Deserialize<LocalWhisperSettings>(config.SettingsJson);
            Assert.NotNull(settings);
            Assert.Equal("http://localhost:9000", settings!.BaseUrl);
        }

        [Fact]
        public void BuildConfig_TrimsNameAndUrl()
        {
            var form = Valid();
            form.Name = "  Whisper  ";
            form.BaseUrl = "  http://localhost:9000  ";

            var config = form.BuildConfig();
            Assert.Equal("Whisper", config.Name);

            var settings = JsonSerializer.Deserialize<LocalWhisperSettings>(config.SettingsJson);
            Assert.Equal("http://localhost:9000", settings!.BaseUrl);
        }

        // ---- FromConfig round-trip ----

        [Fact]
        public void FromConfig_BuildConfig_RoundTrips()
        {
            var original = Valid();
            original.Id = 7;
            var config = original.BuildConfig();

            var round = TranscriptionServiceConfigForm.FromConfig(config);

            Assert.Equal(config.Id, round.Id);
            Assert.Equal(config.Name, round.Name);
            Assert.Equal(config.Type, round.Type);
            Assert.Equal("http://localhost:9000", round.BaseUrl);
        }

        [Fact]
        public void FromConfig_EmptySettingsJson_DoesNotThrow()
        {
            var config = new TranscriptionServiceConfig
            {
                Name = "Empty",
                Type = TranscriptionServiceType.LocalWhisper,
                SettingsJson = "",
            };

            var form = TranscriptionServiceConfigForm.FromConfig(config);
            Assert.Equal("", form.BaseUrl);
        }
    }
}
