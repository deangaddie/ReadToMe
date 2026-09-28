using Read2Me.App.Api;
using Read2Me.AppData.Entities;
using Xunit;

namespace Read2Me.Tests.App
{
    /// <summary>The Qwen3-Base provider's schema on audio.cpp: the model entry and seed join the native knobs.</summary>
    public class Qwen3BaseProviderSettingsTests
    {
        [Fact]
        public void Schema_exposes_the_model_the_native_knobs_and_a_seed()
        {
            var schema = ProviderSettingsSchema.ParagraphTts(ParagraphTtsServiceType.Qwen3Base);

            Assert.Equal("Qwen3Base", schema.Type);
            Assert.Equal(
                ["modelId", "language", "temperature", "top_p", "top_k", "repetition_penalty", "max_new_tokens", "seed"],
                schema.Fields.Select(f => f.Key));
        }

        [Fact]
        public void The_model_defaults_to_qwen3_base_and_a_blank_seed_is_random()
        {
            var fields = ProviderSettingsSchema.ParagraphTts(ParagraphTtsServiceType.Qwen3Base)
                .Fields.ToDictionary(f => f.Key);

            Assert.Equal("qwen3-base", fields["modelId"].Default);
            Assert.Null(fields["seed"].Default);
            Assert.True(fields["seed"].Nullable);
        }
    }
}
