using Read2Me.App.Api;
using Read2Me.AppData.Entities;
using Xunit;

namespace Read2Me.Tests.App
{
    /// <summary>The Qwen3 voice-design provider's schema on audio.cpp: the model entry and seed join the native knobs.</summary>
    public class Qwen3VoiceDesignProviderSettingsTests
    {
        [Fact]
        public void Schema_exposes_the_model_the_native_knobs_and_a_seed()
        {
            var schema = ProviderSettingsSchema.VoiceDesign(VoiceDesignServiceType.Qwen3);

            Assert.Equal("Qwen3", schema.Type);
            Assert.Equal(
                ["modelId", "language", "temperature", "topP", "topK", "repetitionPenalty", "maxNewTokens", "seed"],
                schema.Fields.Select(f => f.Key));
        }

        [Fact]
        public void The_model_defaults_to_qwen3_design_and_a_blank_seed_is_random()
        {
            var fields = ProviderSettingsSchema.VoiceDesign(VoiceDesignServiceType.Qwen3)
                .Fields.ToDictionary(f => f.Key);

            Assert.Equal("qwen3-design", fields["modelId"].Default);
            Assert.Null(fields["seed"].Default);
            Assert.True(fields["seed"].Nullable);
        }
    }
}
