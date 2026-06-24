using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Read2Me.AppData.Entities;
using Read2Me.Services;
using Read2Me.Services.Audio.ParagraphTts;
using Xunit;

namespace Read2Me.Tests.Services.Audio
{
    public class ParagraphTtsClientResolverTests
    {
        private sealed class FakeAudioSettings : AudioProcessingSettingsService
        {
            public FakeAudioSettings()
                : base(null!, null!, NullLogger<AudioProcessingSettingsService>.Instance) { }

            public override Task<AudioProcessingSettings> GetAsync() =>
                Task.FromResult(new AudioProcessingSettings(
                    null, 0.15, SentenceSplitEnabled: false,
                    ChunkPauseMs: 0, VolumePauseMs: 4000, PartPauseMs: 3000,
                    ChapterPauseMs: 2500, ParagraphPauseMs: 800, PauseMs: 500));
        }

        private static ServiceProvider BuildServices(Action<ServiceCollection>? extra = null)
        {
            var sc = new ServiceCollection();
            sc.AddHttpClient();
            sc.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));
            sc.AddKeyedScoped<IParagraphTtsClient, VoxCpm2ParagraphTtsClient>(ParagraphTtsServiceType.VoxCpm2);
            sc.AddSingleton<AudioProcessingSettingsService, FakeAudioSettings>();
            sc.AddScoped<IParagraphTtsClientResolver, ParagraphTtsClientResolver>();
            extra?.Invoke(sc);
            return sc.BuildServiceProvider();
        }

        [Fact]
        public void Resolve_VoxCpm2_OutermostIsTextPreprocessingDecorator()
        {
            using var sp = BuildServices();
            using var scope = sp.CreateScope();

            var resolver = scope.ServiceProvider.GetRequiredService<IParagraphTtsClientResolver>();
            var client = resolver.Resolve(ParagraphTtsServiceType.VoxCpm2);

            // Preprocessing is outermost so it runs before chunking.
            Assert.IsType<TextPreprocessingTtsClient>(client);
        }

        [Fact]
        public void Resolve_UnknownType_Throws()
        {
            var services = new ServiceCollection();
            services.AddScoped<IParagraphTtsClientResolver, ParagraphTtsClientResolver>();

            using var sp = services.BuildServiceProvider();
            using var scope = sp.CreateScope();

            var resolver = scope.ServiceProvider.GetRequiredService<IParagraphTtsClientResolver>();
            var ex = Assert.Throws<NotSupportedException>(() =>
                resolver.Resolve((ParagraphTtsServiceType)999));
            Assert.Contains("999", ex.Message);
        }
    }
}
