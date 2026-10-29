using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Read2Me.App.Services.SetupCheck;
using Read2Me.AppData;
using Read2Me.Services;
using Read2Me.Services.Audio;
using Read2Me.Services.Characters;
using Read2Me.Services.Health;
using Read2Me.Services.Queueing;
using Read2Me.Tests.App.Live;
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

        /// <summary>The Full check's services: real gates, nothing in flight, the real ffmpeg encode unless given one.</summary>
        public static FullCheckServices Full(
            InFlightWork? work = null,
            IWatchdogHoldOff? watchdog = null,
            IFfmpegEncodeCheck? encode = null,
            RecordingHubContext? hub = null,
            IProcessingGate<QueuedParagraph>? characterGate = null,
            IProcessingGate<QueuedAudioItem>? audioGate = null,
            SetupCheckBusy? busy = null) => new(
            characterGate ?? new ProcessingGate<QueuedParagraph>(),
            audioGate ?? new ProcessingGate<QueuedAudioItem>(),
            work ?? new InFlightWork(() => 0, () => 0, () => false, () => false, () => false),
            busy ?? new SetupCheckBusy(),
            watchdog ?? new RecordingHoldOff(),
            encode ?? new FfmpegEncodeCheck(NullLogger<FfmpegEncodeCheck>.Instance),
            hub ?? new RecordingHubContext());
    }

    /// <summary>Records which services are held off now, and every set ever held.</summary>
    internal sealed class RecordingHoldOff : IWatchdogHoldOff
    {
        private readonly List<string> _held = [];

        public List<IReadOnlyCollection<string>> Holds { get; } = [];

        /// <summary>Called as a hold is taken, which is how a test acts at the very start of a run.</summary>
        public Action? OnHoldOff { get; set; }

        public IReadOnlyList<string> Held
        {
            get { lock (_held) return [.. _held]; }
        }

        public IDisposable HoldOff(IReadOnlyCollection<string> services)
        {
            lock (_held) _held.AddRange(services);
            Holds.Add(services);
            OnHoldOff?.Invoke();
            return new Release(this, services);
        }

        private sealed class Release(RecordingHoldOff owner, IReadOnlyCollection<string> services) : IDisposable
        {
            public void Dispose()
            {
                lock (owner._held)
                    foreach (var s in services)
                        owner._held.Remove(s);
            }
        }
    }
}
