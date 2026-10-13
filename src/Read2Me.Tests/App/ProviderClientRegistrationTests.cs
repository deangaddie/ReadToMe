using Microsoft.Extensions.DependencyInjection;
using Read2Me.App.Configuration;
using Read2Me.AppData.Entities;
using Read2Me.Services.Audio.ParagraphTts;
using Read2Me.Services.Audio.VoiceDesign;
using Xunit;

namespace Read2Me.Tests.App
{
    /// <summary>
    /// The wiring from a config's provider type to the client that speaks for it: every provider
    /// type has exactly the client built for it, so an active Breeze, Chatterbox or Qwen3 config
    /// reaches its own audio.cpp request. What each client sends is its *ClientTests class; one
    /// queue run on a real host is AudioApiTests.
    /// </summary>
    public class ProviderClientRegistrationTests
    {
        private static Dictionary<object, Type> KeyedImplementations<TService>()
        {
            var services = new ServiceCollection().AddAudioServices();
            return services
                .Where(d => d.IsKeyedService && d.ServiceType == typeof(TService))
                .ToDictionary(d => d.ServiceKey!, d => d.KeyedImplementationType!);
        }

        [Fact]
        public void Every_paragraph_tts_provider_resolves_to_its_own_client()
        {
            var clients = KeyedImplementations<IParagraphTtsClient>();

            Assert.Equal(
                new Dictionary<object, Type>
                {
                    [ParagraphTtsServiceType.VoxCpm2] = typeof(VoxCpm2ParagraphTtsClient),
                    [ParagraphTtsServiceType.Chatterbox] = typeof(ChatterboxParagraphTtsClient),
                    [ParagraphTtsServiceType.Qwen3Base] = typeof(Qwen3ParagraphTtsClient),
                    [ParagraphTtsServiceType.Breeze] = typeof(BreezeParagraphTtsClient),
                },
                clients);
            Assert.Equal(Enum.GetValues<ParagraphTtsServiceType>().Length, clients.Count);
        }

        [Fact]
        public void Every_voice_design_provider_resolves_to_its_own_client()
        {
            var clients = KeyedImplementations<IVoiceDesignClient>();

            Assert.Equal(
                new Dictionary<object, Type>
                {
                    [VoiceDesignServiceType.VoxCpm2] = typeof(VoxCpm2VoiceDesignClient),
                    [VoiceDesignServiceType.Qwen3] = typeof(Qwen3VoiceDesignClient),
                    [VoiceDesignServiceType.Breeze] = typeof(BreezeVoiceDesignClient),
                },
                clients);
            Assert.Equal(Enum.GetValues<VoiceDesignServiceType>().Length, clients.Count);
        }
    }
}
