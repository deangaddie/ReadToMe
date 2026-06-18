using Read2Me.Services.Audio.VoiceDesign.Settings;
using Xunit;

namespace Read2Me.Tests.Services.Audio
{
    public class VoiceDesignSettingsMergeTests
    {
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
