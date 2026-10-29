using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Read2Me.App.Services.SetupCheck;
using Read2Me.AppData;
using Read2Me.Services;
using Read2Me.Services.Health;
using Read2Me.Tests.Fakes;

namespace Read2Me.Tests.App.SetupCheck
{
    /// <summary>
    /// The runner's <see cref="SetupCheckServices"/> over the test database: the real settings
    /// services in a scope, the service-control fake, and the given HTTP factory.
    /// </summary>
    internal static class SetupCheckTestServices
    {
        public static SetupCheckServices Create(
            IDbContextFactory<Read2MeDbContext> db,
            IAiServiceControl? control = null,
            IHttpClientFactory? http = null,
            AiWatchdogOptions? watchdog = null)
        {
            var scopes = new ServiceCollection()
                .AddScoped(_ => new LlmSettingsService(db, NullLogger<LlmSettingsService>.Instance))
                .AddScoped(_ => new ParagraphTtsSettingsService(db, NullLogger<ParagraphTtsSettingsService>.Instance))
                .AddScoped(_ => new VoiceDesignSettingsService(db, NullLogger<VoiceDesignSettingsService>.Instance))
                .AddScoped(_ => new TranscriptionSettingsService(db, NullLogger<TranscriptionSettingsService>.Instance))
                .AddScoped(_ => new SemanticSimilaritySettingsService(db, NullLogger<SemanticSimilaritySettingsService>.Instance))
                .BuildServiceProvider()
                .GetRequiredService<IServiceScopeFactory>();
            return new SetupCheckServices(
                scopes,
                control ?? new FakeAiServiceControl(),
                new DockerAiServiceRegistry(),
                http ?? new SingleHandlerHttpClientFactory(new FakeAudioCppHandler()),
                Options.Create(watchdog ?? new AiWatchdogOptions()));
        }
    }
}
