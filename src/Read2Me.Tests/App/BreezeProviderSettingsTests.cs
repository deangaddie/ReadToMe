using System.Text.Json;
using System.Text.Json.Nodes;
using Read2Me.App.Api;
using Read2Me.AppData.Entities;
using Read2Me.Services;
using Read2Me.Services.Audio.ParagraphTts.Settings;
using Xunit;

namespace Read2Me.Tests.App
{
    /// <summary>
    /// The Breeze provider's settings surface: the schema the Angular editor renders from, the
    /// canonical blob a write stores, and the base URL pre-flight reads.
    /// </summary>
    public class BreezeProviderSettingsTests
    {
        [Fact]
        public void Schema_exposes_every_setting_but_the_connection_and_chunk_carrier_knobs()
        {
            var schema = ProviderSettingsSchema.ParagraphTts(ParagraphTtsServiceType.Breeze);

            Assert.Equal("Breeze", schema.Type);
            Assert.Equal(
                ["modelId", "instructedGuidanceScale", "plainGuidanceScale", "temperature", "topK", "topP", "seed"],
                schema.Fields.Select(f => f.Key));
        }

        [Fact]
        public void Schema_defaults_are_the_spec_defaults()
        {
            var defaults = ProviderSettingsSchema.ParagraphTts(ParagraphTtsServiceType.Breeze)
                .Fields.ToDictionary(f => f.Key, f => f.Default);

            Assert.Equal("breeze-q8", defaults["modelId"]);
            Assert.Equal(3.0, defaults["instructedGuidanceScale"]);
            Assert.Equal(1.0, defaults["plainGuidanceScale"]);
            Assert.Equal(0.9, defaults["temperature"]);
            Assert.Equal(50, defaults["topK"]);
            Assert.Equal(1.0, defaults["topP"]);
            Assert.Null(defaults["seed"]);
        }

        [Fact]
        public void Seed_may_be_left_blank_for_a_random_seed()
        {
            var seed = ProviderSettingsSchema.ParagraphTts(ParagraphTtsServiceType.Breeze)
                .Fields.Single(f => f.Key == "seed");

            Assert.True(seed.Nullable);
        }

        [Fact]
        public void A_write_is_canonicalised_to_the_record_with_the_chunk_and_carrier_keys_the_wrappers_read()
        {
            var config = new ParagraphTtsServiceConfig
            {
                Type = ParagraphTtsServiceType.Breeze,
                SettingsJson = """{"BASEURL":"http://localhost:8004","seed":7}""",
            };

            ProviderSettingsJson.Canonicalize(config);

            var stored = JsonNode.Parse(config.SettingsJson)!.AsObject();
            Assert.Equal("http://localhost:8004", stored["baseUrl"]!.GetValue<string>());
            Assert.Equal(7, stored["seed"]!.GetValue<int>());
            Assert.Equal("breeze-q8", stored["modelId"]!.GetValue<string>());
            // The chunking and carrier wrappers read these as VoxCpm2ParagraphTtsSettings, case-sensitively.
            var shared = JsonSerializer.Deserialize<VoxCpm2ParagraphTtsSettings>(config.SettingsJson)!;
            Assert.Equal(500, shared.MaxChunkChars);
            Assert.Equal(30, shared.CarrierMaxTargetChars);
        }

        [Fact]
        public void The_base_url_is_read_from_the_settings()
        {
            var config = new ParagraphTtsServiceConfig
            {
                Type = ParagraphTtsServiceType.Breeze,
                SettingsJson = JsonSerializer.Serialize(BreezeParagraphTtsSettings.Recommended with { BaseUrl = "http://localhost:8004" }),
            };

            Assert.Equal("http://localhost:8004", ServiceConfigBaseUrls.For(config));
        }
    }
}
