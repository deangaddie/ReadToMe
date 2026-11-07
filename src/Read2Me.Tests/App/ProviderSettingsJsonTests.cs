using System.Text.Json;
using System.Text.Json.Nodes;
using Read2Me.App.Api;
using Read2Me.AppData.Entities;
using Read2Me.Services.Audio.ParagraphTts.Settings;
using Xunit;

namespace Read2Me.Tests.App
{
    /// <summary>
    /// The canonical blob a provider-settings write stores: keys normalised to the settings record's
    /// names, the connection kept, and the record's defaults filled in for every key the write left out.
    /// </summary>
    public class ProviderSettingsJsonTests
    {
        [Fact]
        public void A_Breeze_tts_write_is_canonicalised_to_the_record_with_the_chunk_and_carrier_keys_the_wrappers_read()
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
        public void A_Breeze_voice_design_write_is_canonicalised_to_the_record()
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
    }
}
