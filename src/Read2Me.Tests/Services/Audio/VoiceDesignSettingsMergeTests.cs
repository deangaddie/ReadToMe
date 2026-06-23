using Read2Me.Services.Audio.ParagraphTts.Settings;
using Read2Me.Services.Audio.VoiceDesign.Settings;
using Xunit;

namespace Read2Me.Tests.Services.Audio
{
    public class VoiceDesignSettingsMergeTests
    {
        // --- VoxCPM2 paragraph-TTS ---

        private static VoxCpm2ParagraphTtsSettings MergeVoxPara(string defaults, string? overrideJson)
            => VoiceDesignSettingsMerge.Merge<VoxCpm2ParagraphTtsSettings>(defaults, overrideJson);

        private const string VoxParaDefaults =
            """{"baseUrl":"http://localhost:8000","cfg_value":2.0,"inference_timesteps":10,"min_len":2,"max_len":4096,"normalize":false,"denoise":false,"retry_badcase":true,"retry_badcase_max_times":3,"retry_badcase_ratio_threshold":6.0,"maxChunkChars":500}""";

        [Fact]
        public void VoxParaMerge_NullOverride_ReturnsBaseDefaults()
        {
            var r = MergeVoxPara(VoxParaDefaults, null);

            Assert.Equal(2.0, r.CfgValue);
            Assert.Equal(10, r.InferenceTimesteps);
            Assert.Equal(2, r.MinLen);
            Assert.Equal(4096, r.MaxLen);
            Assert.False(r.Normalize);
            Assert.False(r.Denoise);
            Assert.True(r.RetryBadcase);
            Assert.Equal(3, r.RetryBadcaseMaxTimes);
            Assert.Equal(6.0, r.RetryBadcaseRatioThreshold);
            Assert.Equal("http://localhost:8000", r.BaseUrl);
            Assert.Equal(500, r.MaxChunkChars);
        }

        [Fact]
        public void VoxParaMerge_EmptyOverride_ReturnsBaseDefaults()
        {
            var r = MergeVoxPara(VoxParaDefaults, "{}");
            Assert.Equal(2.0, r.CfgValue);
            Assert.Equal(4096, r.MaxLen);
            Assert.Equal(500, r.MaxChunkChars);
        }

        [Fact]
        public void VoxParaMerge_SingleFieldOverride_OnlyThatFieldChanges()
        {
            var r = MergeVoxPara(VoxParaDefaults, """{"cfg_value":3.0}""");

            Assert.Equal(3.0, r.CfgValue);
            Assert.Equal(10, r.InferenceTimesteps);
            Assert.Equal(2, r.MinLen);
            Assert.Equal(4096, r.MaxLen);
            Assert.False(r.Normalize);
            Assert.False(r.Denoise);
            Assert.True(r.RetryBadcase);
            Assert.Equal(3, r.RetryBadcaseMaxTimes);
            Assert.Equal(6.0, r.RetryBadcaseRatioThreshold);
            Assert.Equal(500, r.MaxChunkChars);
        }

        [Fact]
        public void VoxParaMerge_AllNineFields_RoundTripWithCorrectJsonNames()
        {
            const string json = """{"cfg_value":1.5,"inference_timesteps":5,"min_len":3,"max_len":1000,"normalize":true,"denoise":true,"retry_badcase":false,"retry_badcase_max_times":7,"retry_badcase_ratio_threshold":4.2}""";
            var r = MergeVoxPara(json, null);

            Assert.Equal(1.5, r.CfgValue);
            Assert.Equal(5, r.InferenceTimesteps);
            Assert.Equal(3, r.MinLen);
            Assert.Equal(1000, r.MaxLen);
            Assert.True(r.Normalize);
            Assert.True(r.Denoise);
            Assert.False(r.RetryBadcase);
            Assert.Equal(7, r.RetryBadcaseMaxTimes);
            Assert.Equal(4.2, r.RetryBadcaseRatioThreshold);
        }

        // --- VoxCPM2 ---

        private static VoxCpm2VoiceDesignSettings MergeVox(string defaults, string? overrideJson)
            => VoiceDesignSettingsMerge.Merge<VoxCpm2VoiceDesignSettings>(defaults, overrideJson);

        private const string VoxDefaults =
            """{"cfg_value":2.0,"inference_timesteps":10,"min_len":2,"max_len":4096,"normalize":false,"denoise":false,"retry_badcase":true,"retry_badcase_max_times":3,"retry_badcase_ratio_threshold":6.0}""";

        [Fact]
        public void VoxMerge_NullOverride_ReturnsAllNineDefaults()
        {
            var r = MergeVox(VoxDefaults, null);

            Assert.Equal(2.0, r.CfgValue);
            Assert.Equal(10, r.InferenceTimesteps);
            Assert.Equal(2, r.MinLen);
            Assert.Equal(4096, r.MaxLen);
            Assert.False(r.Normalize);
            Assert.False(r.Denoise);
            Assert.True(r.RetryBadcase);
            Assert.Equal(3, r.RetryBadcaseMaxTimes);
            Assert.Equal(6.0, r.RetryBadcaseRatioThreshold);
        }

        [Fact]
        public void VoxMerge_EmptyOverride_ReturnsDefaults()
        {
            var r = MergeVox(VoxDefaults, "{}");
            Assert.Equal(2.0, r.CfgValue);
            Assert.Equal(4096, r.MaxLen);
        }

        [Fact]
        public void VoxMerge_SingleFieldOverride_OnlyThatFieldChanges()
        {
            var r = MergeVox(VoxDefaults, """{"cfg_value":3.5}""");

            Assert.Equal(3.5, r.CfgValue);
            Assert.Equal(10, r.InferenceTimesteps);
            Assert.Equal(2, r.MinLen);
            Assert.Equal(4096, r.MaxLen);
            Assert.False(r.Normalize);
            Assert.False(r.Denoise);
            Assert.True(r.RetryBadcase);
            Assert.Equal(3, r.RetryBadcaseMaxTimes);
            Assert.Equal(6.0, r.RetryBadcaseRatioThreshold);
        }

        [Fact]
        public void VoxMerge_MultiFieldOverride_ExactlyThoseFieldsChange()
        {
            var r = MergeVox(VoxDefaults, """{"inference_timesteps":25,"normalize":true}""");

            Assert.Equal(2.0, r.CfgValue);
            Assert.Equal(25, r.InferenceTimesteps);
            Assert.True(r.Normalize);
            Assert.Equal(4096, r.MaxLen);
        }

        [Fact]
        public void VoxMerge_SnakeCaseKeys_DeserializeIntoCorrectProperties()
        {
            // Proves [JsonPropertyName] attributes are correct
            const string json = """{"cfg_value":1.5,"inference_timesteps":5,"min_len":3,"max_len":1000,"normalize":true,"denoise":true,"retry_badcase":false,"retry_badcase_max_times":7,"retry_badcase_ratio_threshold":4.2}""";
            var r = MergeVox(json, null);

            Assert.Equal(1.5, r.CfgValue);
            Assert.Equal(5, r.InferenceTimesteps);
            Assert.Equal(3, r.MinLen);
            Assert.Equal(1000, r.MaxLen);
            Assert.True(r.Normalize);
            Assert.True(r.Denoise);
            Assert.False(r.RetryBadcase);
            Assert.Equal(7, r.RetryBadcaseMaxTimes);
            Assert.Equal(4.2, r.RetryBadcaseRatioThreshold);
        }


        private static Qwen3VoiceDesignSettings Merge(string defaults, string? overrideJson)
            => VoiceDesignSettingsMerge.Merge<Qwen3VoiceDesignSettings>(defaults, overrideJson);

        private const string Defaults = """{"baseUrl":"http://localhost:8100","apiKey":"key1","model":"qwen3"}""";

        [Fact]
        public void Merge_NullOverride_ReturnsDefaults()
        {
            var result = Merge(Defaults, null);

            Assert.Equal("http://localhost:8100", result.BaseUrl);
            Assert.Equal("key1", result.ApiKey);
            Assert.Equal("qwen3", result.Model);
        }

        [Fact]
        public void Merge_EmptyOverride_ReturnsDefaults()
        {
            var resultEmpty = Merge(Defaults, "");
            var resultEmptyObj = Merge(Defaults, "{}");

            Assert.Equal("http://localhost:8100", resultEmpty.BaseUrl);
            Assert.Equal("http://localhost:8100", resultEmptyObj.BaseUrl);
        }

        [Fact]
        public void Merge_OverrideKey_ReplacesOnlyThatKey()
        {
            var result = Merge(Defaults, """{"model":"qwen3-turbo"}""");

            Assert.Equal("http://localhost:8100", result.BaseUrl);
            Assert.Equal("key1", result.ApiKey);
            Assert.Equal("qwen3-turbo", result.Model);
        }

        [Fact]
        public void Merge_EmptyDefaults_UsesOverrideOnly()
        {
            var result = Merge("", """{"baseUrl":"http://override:9000"}""");

            Assert.Equal("http://override:9000", result.BaseUrl);
        }

        [Fact]
        public void Merge_MalformedOverride_ThrowsJsonException()
        {
            // JsonNode.Parse throws on invalid JSON — documented contract
            // (actual type is JsonReaderException, a subclass of JsonException)
            Assert.ThrowsAny<System.Text.Json.JsonException>(() => Merge(Defaults, "not json"));
        }
    }
}
