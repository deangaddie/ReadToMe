using System.Text.Json;
using System.Text.Json.Nodes;
using Read2Me.App.Api;
using Read2Me.AppData.Entities;
using Read2Me.Services;
using Read2Me.Services.Audio.VoiceDesign.Settings;
using Xunit;

namespace Read2Me.Tests.App
{
    /// <summary>
    /// The Breeze voice-design settings surface: the schema the Angular editor renders from, the
    /// canonical blob a write stores, the base URL pre-flight reads, and the per-voice diff.
    /// </summary>
    public class BreezeVoiceDesignSettingsTests
    {
        [Fact]
        public void Schema_exposes_every_setting_but_the_connection()
        {
            var schema = ProviderSettingsSchema.VoiceDesign(VoiceDesignServiceType.Breeze);

            Assert.Equal("Breeze", schema.Type);
            Assert.Equal(["modelId", "guidanceScale", "seed"], schema.Fields.Select(f => f.Key));
        }

        [Fact]
        public void Schema_defaults_are_the_spec_defaults_and_the_seed_may_be_blank()
        {
            var fields = ProviderSettingsSchema.VoiceDesign(VoiceDesignServiceType.Breeze)
                .Fields.ToDictionary(f => f.Key);

            Assert.Equal("breeze-design", fields["modelId"].Default);
            Assert.Equal(3.0, fields["guidanceScale"].Default);
            Assert.Null(fields["seed"].Default);
            Assert.True(fields["seed"].Nullable);
        }

        [Fact]
        public void A_write_is_canonicalised_to_the_record()
        {
            var config = new VoiceDesignServiceConfig
            {
                Type = VoiceDesignServiceType.Breeze,
                SettingsJson = """{"BASEURL":"http://localhost:8004","seed":7}""",
            };

            ProviderSettingsJson.Canonicalize(config);

            var stored = JsonNode.Parse(config.SettingsJson)!.AsObject();
            Assert.Equal("http://localhost:8004", stored["baseUrl"]!.GetValue<string>());
            Assert.Equal(7, stored["seed"]!.GetValue<int>());
            Assert.Equal("breeze-design", stored["modelId"]!.GetValue<string>());
            Assert.Equal(3.0, stored["guidanceScale"]!.GetValue<double>());
        }

        [Fact]
        public void The_base_url_is_read_from_the_settings()
        {
            var config = new VoiceDesignServiceConfig
            {
                Type = VoiceDesignServiceType.Breeze,
                SettingsJson = JsonSerializer.Serialize(BreezeVoiceDesignSettings.Recommended with { BaseUrl = "http://localhost:8004" }),
            };

            Assert.Equal("http://localhost:8004", ServiceConfigBaseUrls.For(config));
        }

        [Fact]
        public void Diff_keeps_only_the_changed_keys_and_never_the_base_url()
        {
            var baseJson = JsonSerializer.Serialize(BreezeVoiceDesignSettings.Recommended with { BaseUrl = "http://localhost:8004" });
            var edited = BreezeVoiceDesignSettings.Recommended with { BaseUrl = "http://elsewhere", GuidanceScale = 2.5 };

            var diff = JsonNode.Parse(BreezeVoiceDesignSettingsDiff.Diff(baseJson, edited))!.AsObject();

            Assert.Equal(["guidanceScale"], diff.Select(kv => kv.Key));
            Assert.Equal(2.5, diff["guidanceScale"]!.GetValue<double>());
        }

        [Fact]
        public void Apply_merges_a_diff_back_over_the_defaults()
        {
            var baseJson = JsonSerializer.Serialize(BreezeVoiceDesignSettings.Recommended with { BaseUrl = "http://localhost:8004" });

            var applied = BreezeVoiceDesignSettingsDiff.Apply(baseJson, """{"seed":11}""");

            Assert.Equal(11, applied.Seed);
            Assert.Equal("http://localhost:8004", applied.BaseUrl);
            Assert.Equal("breeze-design", applied.ModelId);
        }
    }
}
