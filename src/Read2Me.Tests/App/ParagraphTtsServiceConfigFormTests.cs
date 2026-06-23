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

        // ---- SettingsJson carries all 9 params + BaseUrl + MaxChunkChars ----

        [Fact]
        public void BuildConfig_SettingsJsonContainsAllNineParamsPlusBaseUrlAndChunkChars()
        {
            var form = Valid();
            form.BaseUrl = "http://localhost:8000";
            form.MaxChunkChars = 250;
            // Editor binds full settings object (the 9 tunable params) to SettingsJson.
            form.SettingsJson = JsonSerializer.Serialize(VoxCpm2ParagraphTtsSettings.Recommended with
            {
                CfgValue = 3.5,
                InferenceTimesteps = 20,
                MinLen = 5,
                MaxLen = 2048,
                Normalize = true,
                Denoise = true,
                RetryBadcase = false,
                RetryBadcaseMaxTimes = 7,
                RetryBadcaseRatioThreshold = 4.0,
            });

            var config = form.BuildConfig();
            var s = JsonSerializer.Deserialize<VoxCpm2ParagraphTtsSettings>(config.SettingsJson);

            Assert.NotNull(s);
            Assert.Equal("http://localhost:8000", s!.BaseUrl);
            Assert.Equal(250, s.MaxChunkChars);
            Assert.Equal(3.5, s.CfgValue);
            Assert.Equal(20, s.InferenceTimesteps);
            Assert.Equal(5, s.MinLen);
            Assert.Equal(2048, s.MaxLen);
            Assert.True(s.Normalize);
            Assert.True(s.Denoise);
            Assert.False(s.RetryBadcase);
            Assert.Equal(7, s.RetryBadcaseMaxTimes);
            Assert.Equal(4.0, s.RetryBadcaseRatioThreshold);
        }

        [Fact]
        public void FromConfig_BuildConfig_RoundTripsAllNineParams()
        {
            var original = VoxCpm2ParagraphTtsSettings.Recommended with
            {
                BaseUrl = "http://localhost:8000",
                CfgValue = 2.7,
                InferenceTimesteps = 15,
                MinLen = 4,
                MaxLen = 1024,
                Normalize = true,
                Denoise = true,
                RetryBadcase = false,
                RetryBadcaseMaxTimes = 5,
                RetryBadcaseRatioThreshold = 8.0,
                MaxChunkChars = 333,
            };
            var config = new ParagraphTtsServiceConfig
            {
                Name = "Provider",
                Type = ParagraphTtsServiceType.VoxCpm2,
                SettingsJson = JsonSerializer.Serialize(original),
            };

            var form = ParagraphTtsServiceConfigForm.FromConfig(config);
            var rebuilt = form.BuildConfig();
            var s = JsonSerializer.Deserialize<VoxCpm2ParagraphTtsSettings>(rebuilt.SettingsJson);

            Assert.Equal(original, s);
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
