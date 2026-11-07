using System.Text.Json;
using System.Text.Json.Nodes;
using Read2Me.Services.Audio.VoiceDesign.Settings;
using Xunit;

namespace Read2Me.Tests.Services.Audio.VoiceDesign
{
    /// <summary>The per-voice Breeze voice-design override: only the changed keys, merged back over the config.</summary>
    public class BreezeVoiceDesignSettingsDiffTests
    {
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
