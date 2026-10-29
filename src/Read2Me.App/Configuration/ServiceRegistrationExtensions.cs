using System.IO;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Read2Me.App.Api;
using Read2Me.App.Audio;
using Read2Me.App.Characters;
using Read2Me.App.Queueing;
using Read2Me.App.Services.Preflight;
using Read2Me.App.Services.SetupCheck;
using Microsoft.Extensions.Hosting;
using Read2Me.Services.Queueing;
using Read2Me.Core.Configuration;
using Read2Me.Core.IO;
using Read2Me.AppData;
using Read2Me.Data;
using Read2Me.Services;
using Read2Me.Services.Mutations;
using Read2Me.Services.Audio;
using Read2Me.Services.Audio.Assembly;
using Read2Me.Services.Audio.AudioCpp;
using Read2Me.Services.Audio.ParagraphTts;
using Read2Me.Services.Audio.SemanticSimilarity;
using Read2Me.Services.Audio.Transcription;
using Read2Me.Services.Audio.VoiceDesign;
using Read2Me.Services.Books;
using Read2Me.Services.Characters;
using Read2Me.Services.Characters.ChapterPass;
using Read2Me.Services.Events;
using Read2Me.Services.Health;
using Read2Me.Services.History;
using Read2Me.Services.IO;
using Read2Me.Services.NodeStatus;
using Read2Me.Services.Llm;
using Read2Me.Services.UseCases;
using Read2Me.Services.Text;
using Read2Me.Services.Voice;

namespace Read2Me.App.Configuration;

public static class ServiceRegistrationExtensions
{
    public static IServiceCollection AddProjectServices(this IServiceCollection services, IConfiguration configuration)
    {
        // Not validated: an unusable Workspace starts the host in no-Workspace mode (WorkspaceStart),
        // where the Setup check says what to fix.
        services.AddOptions<WorkspaceOptions>()
            .Bind(configuration.GetSection(WorkspaceOptions.SectionName));
        services.AddSingleton<WorkspaceStart>();
        services.AddSingleton<IFileSystem, FileSystemService>();
        services.AddSingleton<IProjectDbContextFactory, ProjectDbContextProvider>();
        services.AddScoped<ProjectDbSession>();
        services.AddScoped<ProjectService>();
        services.AddScoped<IProjectWriter>(sp => sp.GetRequiredService<ProjectService>());
        services.AddScoped<ProjectReader>();
        services.AddScoped<IProjectReader>(sp => sp.GetRequiredService<ProjectReader>());
        services.AddScoped<IProjectCatalogReader>(sp => sp.GetRequiredService<ProjectReader>());
        services.AddScoped<IBookContentReader>(sp => sp.GetRequiredService<ProjectReader>());
        services.AddScoped<ICharacterReader>(sp => sp.GetRequiredService<ProjectReader>());
        services.AddScoped<IUnattributedItemCounter>(sp => sp.GetRequiredService<ProjectReader>());
        services.AddScoped<IAudioItemReader>(sp => sp.GetRequiredService<ProjectReader>());
        services.AddScoped<IVoiceResolver, VoiceResolver>();
        services.AddScoped<IVoiceRulePreview, VoiceRulePreview>();
        services.AddBookCommandHandlers();
        services.Configure<BookMutationOptions>(configuration.GetSection(BookMutationOptions.SectionName));
        services.Configure<HistoryOptions>(configuration.GetSection(HistoryOptions.SectionName));
        
        services.AddScoped<BookCommandApiAdapter>();

        services.AddScoped<BookReadingService>();
        services.AddScoped<ProjectUseCases>();
        services.AddScoped<BookUseCases>();
        services.AddScoped<EnqueueUseCases>();

        services.AddSingleton<EpubFileReader>();
        services.AddSingleton<TextFileReader>();

        return services;
    }

    public static IServiceCollection AddLlmServices(this IServiceCollection services)
    {
        services.AddScoped<LlmSettingsService>();
        services.AddScoped<LlmPromptService>();
        services.AddSingleton<IModelLoadGate, ModelLoadGate>();
        services.AddScoped<ILlmClient, OpenAiLlmClient>();
        services.AddScoped<ILlmCompletionRunner, LlmCompletionRunner>();
        services.AddSingleton<EventBroadcaster<LlmStreamEvent>>();
        services.AddSingleton(sp => new EventJournal<LlmStreamEvent>(
            sp.GetRequiredService<EventBroadcaster<LlmStreamEvent>>(),
            e => e is RequestStarted));
        // Its own family, deliberately: one of these rides every token, and every LlmStreamEvent
        // subscriber repaints or journals whatever it receives. Not journalled — a reading is only
        // meaningful live, and replaying a turn's worth to a late subscriber would chart the past
        // as the present. See LlmTimingsSample.
        services.AddSingleton<EventBroadcaster<LlmTimingsSample>>();
        // App-scoped: one queue runs at a time on one GPU, so every client should read the same
        // totals. Resolved eagerly at startup, because it only sees the events published after it
        // subscribes — a lazily-created aggregator would miss the run that created it.
        services.AddSingleton<ThroughputAggregator>();
        return services;
    }

    public static IServiceCollection AddAudioQueueServices(this IServiceCollection services)
    {
        services.AddSingleton<Read2Me.Services.Audio.AudioQueueService>();
        services.AddSingleton<IQueueSource<Read2Me.Services.Audio.QueuedAudioItem>>(
            sp => sp.GetRequiredService<Read2Me.Services.Audio.AudioQueueService>());
        services.AddSingleton<Read2Me.Services.Audio.IAudioQueue>(
            sp => sp.GetRequiredService<Read2Me.Services.Audio.AudioQueueService>());
        services.AddScoped<IAudioQueueProcessor, AudioQueueProcessor>();
        services.AddScoped<IQueueProcessor<Read2Me.Services.Audio.QueuedAudioItem>>(
            sp => sp.GetRequiredService<IAudioQueueProcessor>());
        services.AddSingleton<IProcessingGate<Read2Me.Services.Audio.QueuedAudioItem>, ProcessingGate<Read2Me.Services.Audio.QueuedAudioItem>>();
        services.AddWorkspaceHostedService<QueueWorker<Read2Me.Services.Audio.QueuedAudioItem>>();
        return services;
    }

    public static IServiceCollection AddAudioServices(this IServiceCollection services)
    {
        services.AddScoped<VoiceDesignSettingsService>();
        services.AddScoped<TranscriptionSettingsService>();
        services.AddScoped<SemanticSimilaritySettingsService>();
        services.AddScoped<ISemanticVerifier, SemanticVerifier>();
        services.AddScoped<ISemanticSimilarityClientResolver, SemanticSimilarityClientResolver>();
        services.AddKeyedScoped<ISemanticSimilarityClient, SemanticSimilarityClient>(Read2Me.AppData.Entities.SemanticSimilarityServiceType.MiniLmL6);
        services.AddKeyedScoped<ISemanticSimilarityClient, SemanticSimilarityClient>(Read2Me.AppData.Entities.SemanticSimilarityServiceType.MpnetBaseV2);
        services.AddScoped<ParagraphTtsSettingsService>();
        services.AddSingleton<IFfmpegProber, FfmpegProber>(); // stateless; the Setup check runner (a singleton) uses it
        services.AddScoped<AudioProcessingSettingsService>();
        services.AddSingleton<IWerComparer, WerComparer>();
        services.AddSingleton<Read2Me.Services.Audio.AudioReviewService>();
        services.AddSingleton<Read2Me.Services.Events.EventBroadcaster<Read2Me.Services.Audio.AudioGenEvent>>();
        services.AddSingleton(sp => new Read2Me.Services.Events.EventJournal<Read2Me.Services.Audio.AudioGenEvent>(
            sp.GetRequiredService<Read2Me.Services.Events.EventBroadcaster<Read2Me.Services.Audio.AudioGenEvent>>(),
            e => e is Read2Me.Services.Audio.ItemStarted));
        services.AddScoped<IAudioNormalizer, FfmpegAudioNormalizer>();
        services.AddScoped<IAudioPostProcessStepCatalog, AudioPostProcessStepCatalog>();
        services.AddScoped<IAudioPostProcessStep, SilenceTrimStep>();
        services.AddScoped<IAudioPostProcessStep, ConsonantSoftenStep>();
        // Voice-scope only — the paragraph catalog never sees them (AudioPostProcessStepDefaults).
        services.AddScoped<IAudioPostProcessStep, DePlosiveStep>();
        services.AddScoped<IAudioPostProcessStep, DenoiseStep>();
        services.AddScoped<IAudioPostProcessStep, HissReduceStep>();
        services.AddScoped<IAudioPostProcessChain, AudioPostProcessChain>();
        services.AddSingleton<AudioPreviewStore>();
        services.AddScoped<IPreviewSourceCache, PreviewSourceCache>();
        services.AddScoped<IPreviewChainRenderer, PreviewChainRenderer>();
        services.AddScoped<IAudioPostProcessPreviewRenderer, AudioPostProcessPreviewRenderer>();
        services.AddScoped<IVoicePreviewRenderer, VoicePreviewRenderer>();
        services.AddScoped<IVoiceOriginalStore, VoiceOriginalStore>();
        services.AddScoped<IVoiceAudioEditor, VoiceAudioEditor>();
        // Process-wide, not request-scoped: the HTTP API renders a preview in one request and applies it in another.
        services.AddSingleton<IPreviewStore>(_ => new PreviewStore(TimeProvider.System));
        services.AddScoped<IRecentAudioSampleFinder, RecentAudioSampleFinder>();
        services.AddSingleton<Read2Me.Services.Events.EventBroadcaster<Read2Me.Services.Audio.Assembly.AssemblyEvent>>();
        services.AddSingleton<IAudiobookEncoder, AudiobookEncoder>();
        services.AddSingleton<AudiobookAssemblyService>();
        services.AddScoped<ITranscriptionClientResolver, TranscriptionClientResolver>();
        services.AddKeyedScoped<ITranscriptionClient, WhisperTranscriptionClient>(Read2Me.AppData.Entities.TranscriptionServiceType.LocalWhisper);
        services.AddScoped<IVoiceDesignClientResolver, VoiceDesignClientResolver>();
        services.AddScoped<VoiceAudioGenerator>();
        services.AddScoped<IVoiceAudioGenerator>(sp => sp.GetRequiredService<VoiceAudioGenerator>());
        services.AddKeyedScoped<IVoiceDesignClient, VoxCpm2VoiceDesignClient>(Read2Me.AppData.Entities.VoiceDesignServiceType.VoxCpm2);
        services.AddKeyedScoped<IVoiceDesignClient, Qwen3VoiceDesignClient>(Read2Me.AppData.Entities.VoiceDesignServiceType.Qwen3);
        services.AddKeyedScoped<IVoiceDesignClient, BreezeVoiceDesignClient>(Read2Me.AppData.Entities.VoiceDesignServiceType.Breeze);
        services.AddScoped<Read2Me.Core.Audio.IAudioPipeline, FileAudioPipeline>();
        // The arriving half of the Voice audio ordering rule: it needs the pipeline above, which is
        // an application service, so it is registered here rather than with the mutation wiring.
        services.AddScoped<Read2Me.Services.Audio.IVoiceAudioWriter, Read2Me.Services.Audio.VoiceAudioWriter>();
        services.AddScoped<IAudioItemPipeline, AudioItemPipeline>();
        services.AddScoped<IAudioItemResolver, AudioItemResolver>();
        services.AddScoped<IAudioResultRecorder, AudioResultRecorder>();
        services.AddScoped<VoiceDesignPromptService>();
        services.AddScoped<IParagraphTtsClientResolver, ParagraphTtsClientResolver>();
        services.AddKeyedScoped<IParagraphTtsClient, VoxCpm2ParagraphTtsClient>(Read2Me.AppData.Entities.ParagraphTtsServiceType.VoxCpm2);
        services.AddKeyedScoped<IParagraphTtsClient, ChatterboxParagraphTtsClient>(Read2Me.AppData.Entities.ParagraphTtsServiceType.Chatterbox);
        services.AddKeyedScoped<IParagraphTtsClient, Qwen3ParagraphTtsClient>(Read2Me.AppData.Entities.ParagraphTtsServiceType.Qwen3Base);
        services.AddKeyedScoped<IParagraphTtsClient, BreezeParagraphTtsClient>(Read2Me.AppData.Entities.ParagraphTtsServiceType.Breeze);
        // audio.cpp TTS runtime: the gate is a singleton so its per-endpoint locks span every scope.
        services.AddHttpClient(AudioCppClient.HttpClientName, c => c.Timeout = AudioCppClient.HttpTimeout);
        services.AddSingleton(AudioCppRetryPolicy.Default);
        services.AddSingleton<IAudioCppGate, AudioCppGate>();
        services.AddSingleton<IAudioCppClient, AudioCppClient>();
        services.AddSingleton<ITextProcessingStepCatalog, TextProcessingStepCatalog>();
        services.AddSingleton(new TextProcessingStepDescriptor(
            "to-sentence-case",
            "Sentence case (de-shout all-caps)",
            "Converts fully all-caps paragraphs to sentence case and lowercases long all-caps words."));
        services.AddScoped<ITextSubstitutionStepSource, DbTextSubstitutionStepSource>();
        services.AddScoped<IBuiltInStepSource, DbBuiltInStepSource>();
        return services;
    }

    public static IServiceCollection AddCharacterServices(this IServiceCollection services)
    {
        services.AddSingleton<CharacterQueueService>();
        services.AddSingleton<IParagraphQueueProbe>(
            sp => sp.GetRequiredService<CharacterQueueService>());
        services.AddSingleton<ICharacterQueue>(
            sp => sp.GetRequiredService<CharacterQueueService>());
        services.AddSingleton<NodeStatusService>();
        services.AddSingleton<Read2Me.App.Live.AttributionProgressState>();
        services.AddSingleton<IQueueSource<QueuedParagraph>>(
            sp => sp.GetRequiredService<CharacterQueueService>());
        services.AddScoped<ICharacterQueueProcessor, CharacterQueueProcessor>();
        services.AddScoped<IQueueProcessor<QueuedParagraph>>(
            sp => sp.GetRequiredService<ICharacterQueueProcessor>());
        services.AddSingleton<IProcessingGate<QueuedParagraph>, ProcessingGate<QueuedParagraph>>();
        services.AddWorkspaceHostedService<QueueWorker<QueuedParagraph>>();
        services.AddScoped<AttributionRequestBuilder>();
        services.AddScoped<CharacterAttributionService>();
        services.AddScoped<ChapterAttributionStep>();
        // The walk sees one step: the router sends a Chapter-style rung to the chapter pass and
        // every other style, untouched, to the existing step.
        services.AddScoped<IChainStep>(sp => new StyleRoutedChainStep(
            sp.GetRequiredService<CharacterAttributionService>(),
            sp.GetRequiredService<ChapterAttributionStep>()));
        services.AddScoped<AttributionEscalationChain>();
        services.AddScoped<Read2Me.App.Services.VoiceOrchestrator>();
        services.AddSingleton<EventBroadcaster<VoiceBatchEvent>>();
        services.AddSingleton<VoiceBatchRunner>();
        // History's Background runs follow the Character Queue and the voice batch.
        services.AddSingleton<BackgroundRunTracker>();
        services.AddWorkspaceHostedService(sp => sp.GetRequiredService<BackgroundRunTracker>());
        // Reads the queues, the voice batch and the Assembly, all singletons registered in this file.
        services.AddSingleton<ProjectBusyAnswer>();
        services.AddSingleton<IProjectBusyAnswer>(sp => sp.GetRequiredService<ProjectBusyAnswer>());

        // AI book edits
        services.AddScoped<Read2Me.Services.Llm.ChapterOutlineBuilder>();
        services.AddScoped<Read2Me.Services.BookEdits.ScopeResolver>();
        services.AddScoped<Read2Me.Services.BookEdits.BookEditPlanner>();
        services.AddScoped<Read2Me.Services.BookEdits.BookEditProposalService>();
        services.AddSingleton<Read2Me.App.Live.BookEditRunCoordinator>();
        services.AddSingleton<Read2Me.App.Live.LlmTestRunCoordinator>();

        // Character discovery
        services.AddScoped<Read2Me.Services.Characters.CharacterDiscoveryService>();
        return services;
    }

    public static IServiceCollection AddAiWatchdogServices(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<AiWatchdogOptions>(configuration.GetSection(AiWatchdogOptions.SectionName));
        services.AddSingleton<DockerAiServiceRegistry>();
        services.AddSingleton<IProcessRunner, ProcessRunner>();
        services.AddSingleton<IContainerController, DockerCliContainerController>();
        services.AddSingleton<IAiServiceProbe, AiServiceProbe>();
        services.AddSingleton<EventBroadcaster<WatchdogEvent>>();
        services.AddSingleton(BuildGateMap);
        services.AddSingleton<AiServiceHealthMonitor>();
        services.AddSingleton<IAiServiceReporter, AiServiceReporter>();
        services.AddSingleton<IAiServiceControl, AiServiceControl>();
        // Status observations for /hubs/live: every UI-facing probe and op (API, pre-flight) goes
        // through the observed facade so every client's chips follow (Angular ticket 25).
        services.AddSingleton<EventBroadcaster<ServiceStatusChanged>>();
        services.AddSingleton(sp => new ObservedAiServiceControl(
            sp.GetRequiredService<IAiServiceControl>(), sp.GetRequiredService<EventBroadcaster<ServiceStatusChanged>>()));
        services.AddSingleton<Read2Me.App.Live.AiServiceOpCoordinator>();
        services.AddSingleton<Read2Me.App.Live.PreflightRunCoordinator>();
        // Scoped: the resolver reads the scoped settings services.
        services.AddScoped<IAiTaskRequirementsResolver, AiTaskRequirementsResolver>();
        services.AddScoped<IAiPreflightPlanner>(sp => new AiPreflightPlanner(
            sp.GetRequiredService<IAiTaskRequirementsResolver>(),
            sp.GetRequiredService<ObservedAiServiceControl>(),
            sp.GetRequiredService<DockerAiServiceRegistry>()));
        return services;
    }

    // Maps each registered service to the gate(s) recovery must hold (see WatchdogGateMap.ForServices).
    private static WatchdogGateMap BuildGateMap(System.IServiceProvider sp)
    {
        var registry = sp.GetRequiredService<DockerAiServiceRegistry>();
        IWatchdogGate paragraphGate = new ProcessingGateAdapter<QueuedParagraph>(
            sp.GetRequiredService<IProcessingGate<QueuedParagraph>>(),
            sp.GetRequiredService<IQueueSource<QueuedParagraph>>());
        IWatchdogGate audioGate = new ProcessingGateAdapter<Read2Me.Services.Audio.QueuedAudioItem>(
            sp.GetRequiredService<IProcessingGate<Read2Me.Services.Audio.QueuedAudioItem>>(),
            sp.GetRequiredService<IQueueSource<Read2Me.Services.Audio.QueuedAudioItem>>());

        return WatchdogGateMap.ForServices(registry.All, paragraphGate, audioGate);
    }

    /// <summary>The Setup check runner (it shares the watchdog's process runner) and its start-up Quick check.</summary>
    public static IServiceCollection AddSetupCheck(this IServiceCollection services)
    {
        services.AddSingleton<ISetupCheckRunner>(sp => new SetupCheckRunner(
            sp.GetRequiredService<IConfiguration>(),
            sp.GetRequiredService<IHostEnvironment>().ContentRootPath,
            sp.GetRequiredService<IDbContextFactory<Read2MeDbContext>>(),
            sp.GetRequiredService<IFfmpegProber>(),
            sp.GetRequiredService<IProcessRunner>(),
            sp.GetRequiredService<WorkspaceStart>(),
            // The observed control, so each status the check reads also refreshes the live status chips.
            new SetupCheckServices(
                sp.GetRequiredService<IServiceScopeFactory>(),
                sp.GetRequiredService<ObservedAiServiceControl>(),
                sp.GetRequiredService<DockerAiServiceRegistry>(),
                sp.GetRequiredService<IHttpClientFactory>(),
                sp.GetRequiredService<IOptions<AiWatchdogOptions>>())));
        services.AddHostedService<SetupCheckAtStart>();
        return services;
    }

    public static IServiceCollection AddAppState(this IServiceCollection services)
    {
        services.AddSingleton<ThemeService>();
        return services;
    }

    public static IServiceCollection AddAppDatabase(this IServiceCollection services)
    {
        services.AddDbContextFactory<Read2MeDbContext>((sp, options) =>
        {
            var workspace = sp.GetRequiredService<WorkspaceStart>();
            if (workspace.Usable)
            {
                options.UseSqlite($"Data Source={Path.Combine(workspace.FolderPath, "app.db")}");
                return;
            }
            // No-Workspace mode: the factory is still built (the Setup check runner takes it), but
            // nothing may create an app.db beside the process, so any open fails instead.
            options.UseSqlite(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(Path.GetTempPath(), $"read2me-no-workspace-{Guid.NewGuid():N}", "app.db"),
                Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly,
            }.ToString());
        });
        return services;
    }
}
