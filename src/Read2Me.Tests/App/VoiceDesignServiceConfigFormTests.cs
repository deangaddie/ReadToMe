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
    }
}
