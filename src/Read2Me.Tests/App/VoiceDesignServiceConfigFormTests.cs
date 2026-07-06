using Read2Me.App.Shared;
using Read2Me.AppData.Entities;
using Read2Me.Services;
using Xunit;

namespace Read2Me.Tests.App
{
    public class VoiceDesignServiceConfigFormTests
    {
        /// <summary>
        /// Regression: the VoxCpm2 form serializes SettingsJson with Web options ("baseUrl"),
        /// while pre-flight's <see cref="ServiceConfigBaseUrls"/> parsed case-sensitively and got
        /// null back — so the endpoint looked unmanaged and the GPU-swap dialog (stop llama,
        /// start voxcpm2) never appeared before voice audio generation.
        /// </summary>
        [Theory]
        [InlineData(VoiceDesignServiceType.VoxCpm2, "http://localhost:8003")]
        [InlineData(VoiceDesignServiceType.Qwen3, "http://localhost:8100")]
        public void BuildConfig_SettingsJson_ResolvesBaseUrlForPreflight(VoiceDesignServiceType type, string url)
        {
            var form = new VoiceDesignServiceConfigForm
            {
                Name = "test",
                Type = type,
                BaseUrl = url,
            };

            var config = form.BuildConfig();

            Assert.Equal(url, ServiceConfigBaseUrls.For(config));
        }

        [Theory]
        [InlineData(VoiceDesignServiceType.VoxCpm2)]
        [InlineData(VoiceDesignServiceType.Qwen3)]
        public void BuildConfig_RoundTripsThroughFromConfig(VoiceDesignServiceType type)
        {
            var form = new VoiceDesignServiceConfigForm
            {
                Name = "test",
                Type = type,
                BaseUrl = "http://localhost:8003",
            };

            var reloaded = VoiceDesignServiceConfigForm.FromConfig(form.BuildConfig());

            Assert.Equal("http://localhost:8003", reloaded.BaseUrl);
            Assert.Equal(type, reloaded.Type);
        }

        [Fact]
        public void BuildConfig_Qwen3_RoundTripsLanguageAndSamplingParams()
        {
            var form = new VoiceDesignServiceConfigForm
            {
                Name = "test",
                Type = VoiceDesignServiceType.Qwen3,
                BaseUrl = "http://localhost:8100",
                Language = "ja",
                Temperature = 0.6,
                TopP = 0.9,
                TopK = 40,
                RepetitionPenalty = 1.1,
                MaxNewTokens = 512,
            };

            var reloaded = VoiceDesignServiceConfigForm.FromConfig(form.BuildConfig());

            Assert.Equal("ja", reloaded.Language);
            Assert.Equal(0.6, reloaded.Temperature);
            Assert.Equal(0.9, reloaded.TopP);
            Assert.Equal(40, reloaded.TopK);
            Assert.Equal(1.1, reloaded.RepetitionPenalty);
            Assert.Equal(512, reloaded.MaxNewTokens);
        }
    }
}
