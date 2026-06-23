using System.Text.Json;
using Read2Me.App.Shared;
using Read2Me.AppData.Entities;
using Read2Me.Services.Audio.ParagraphTts.Settings;
using Xunit;

namespace Read2Me.Tests.App
{
    public class ParagraphTtsServiceConfigFormTests
    {
        private static ParagraphTtsServiceConfigForm Valid() => new()
        {
            Name = "Local VoxCpm2",
            Type = ParagraphTtsServiceType.VoxCpm2,
            BaseUrl = "http://localhost:8000",
            MaxChunkChars = 500,
        };

        // ---- MaxChunkChars default ----

        [Fact]
        public void FromConfig_SettingsJsonLackingMaxChunkChars_DefaultsTo500()
        {
            var json = JsonSerializer.Serialize(new { BaseUrl = "http://localhost:8000", MaxLen = 4096 });
            var config = new ParagraphTtsServiceConfig
            {
                Name = "Old Provider",
                Type = ParagraphTtsServiceType.VoxCpm2,
                SettingsJson = json,
            };

            var form = ParagraphTtsServiceConfigForm.FromConfig(config);

            Assert.Equal(500, form.MaxChunkChars);
        }

        // ---- BuildConfig serializes MaxChunkChars ----

        [Fact]
        public void BuildConfig_SerializesMaxChunkCharsIntoSettingsJson()
        {
            var form = Valid();
            form.MaxChunkChars = 250;

            var config = form.BuildConfig();

            var settings = JsonSerializer.Deserialize<VoxCpm2ParagraphTtsSettings>(config.SettingsJson);
            Assert.NotNull(settings);
            Assert.Equal(250, settings!.MaxChunkChars);
        }

        // ---- Round-trip ----

        [Fact]
        public void FromConfig_BuildConfig_RoundTripsMaxChunkChars()
        {
            var form = Valid();
            form.MaxChunkChars = 750;

            var config = form.BuildConfig();
            var round = ParagraphTtsServiceConfigForm.FromConfig(config);

            Assert.Equal(750, round.MaxChunkChars);
        }

        // ---- FromConfig reads MaxChunkChars ----

        [Fact]
        public void FromConfig_ReadsMaxChunkCharsFromSettingsJson()
        {
            var json = JsonSerializer.Serialize(new VoxCpm2ParagraphTtsSettings
            {
                BaseUrl = "http://localhost:8000",
                MaxLen = 4096,
                MaxChunkChars = 1200,
            });
            var config = new ParagraphTtsServiceConfig
            {
                Name = "Provider",
                Type = ParagraphTtsServiceType.VoxCpm2,
                SettingsJson = json,
            };

            var form = ParagraphTtsServiceConfigForm.FromConfig(config);

            Assert.Equal(1200, form.MaxChunkChars);
        }
    }
}
