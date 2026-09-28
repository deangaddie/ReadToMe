using Read2Me.App.Api;
using Read2Me.AppData.Entities;
using Xunit;

namespace Read2Me.Tests.App
{
    /// <summary>The Chatterbox provider's schema on audio.cpp: the model entry and seed join the native knobs.</summary>
    public class ChatterboxProviderSettingsTests
    {
        [Fact]
        public void Schema_exposes_the_model_the_native_knobs_and_a_seed()
        {
            var schema = ProviderSettingsSchema.ParagraphTts(ParagraphTtsServiceType.Chatterbox);

            Assert.Equal("Chatterbox", schema.Type);
            Assert.Equal(
                ["modelId", "exaggeration", "cfg_weight", "temperature", "min_p", "top_p", "repetition_penalty", "seed"],
                schema.Fields.Select(f => f.Key));
        }

        [Fact]
        public void The_model_defaults_to_chatterbox_and_a_blank_seed_is_random()
        {
            var fields = ProviderSettingsSchema.ParagraphTts(ParagraphTtsServiceType.Chatterbox)
                .Fields.ToDictionary(f => f.Key);

            Assert.Equal("chatterbox", fields["modelId"].Default);
            Assert.Null(fields["seed"].Default);
            Assert.True(fields["seed"].Nullable);
        }
    }
}
