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
        [Fact]
        public void Resolve_VoxCpm2_ReturnsClientWrappedInSentenceChunkedDecorator()
        {
            var services = new ServiceCollection();
            services.AddHttpClient();
            services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));
            services.AddKeyedScoped<IParagraphTtsClient, VoxCpm2ParagraphTtsClient>(ParagraphTtsServiceType.VoxCpm2);
            services.AddSingleton(new AudioProcessingSettingsService(null!, null!, NullLogger<AudioProcessingSettingsService>.Instance));
            services.AddScoped<IParagraphTtsClientResolver, ParagraphTtsClientResolver>();

            using var sp = services.BuildServiceProvider();
            using var scope = sp.CreateScope();

            var resolver = scope.ServiceProvider.GetRequiredService<IParagraphTtsClientResolver>();
            var client = resolver.Resolve(ParagraphTtsServiceType.VoxCpm2);

            // Every registered type is wrapped so chunking applies uniformly.
            Assert.IsType<SentenceChunkedTtsClient>(client);
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
