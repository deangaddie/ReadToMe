using System.IO;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Read2Me.App.Audio;
using Read2Me.App.Characters;
using Read2Me.App.Queueing;
using Read2Me.App.Services.Preflight;
using Read2Me.App.State;
using Read2Me.Services.Queueing;
using Read2Me.Core.Configuration;
using Read2Me.Core.IO;
using Read2Me.App.Shared.BookMenus;
using Read2Me.AppData;
using Read2Me.Data;
using Read2Me.Services;
using Read2Me.Services.Audio;
using Read2Me.Services.Audio.Assembly;
using Read2Me.Services.Audio.ParagraphTts;
using Read2Me.Services.Audio.SemanticSimilarity;
using Read2Me.Services.Audio.Transcription;
using Read2Me.Services.Audio.VoiceDesign;
using Read2Me.Services.Books;
using Read2Me.Services.Characters;
using Read2Me.Services.Events;
using Read2Me.Services.Health;
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
        services.Configure<WorkspaceOptions>(configuration.GetSection(WorkspaceOptions.SectionName));
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
        services.AddScoped<IAudioItemReader>(sp => sp.GetRequiredService<ProjectReader>());
        services.AddScoped<IVoiceResolver, VoiceResolver>();
        services.AddBookCommandHandlers();
        
        services.AddScoped<BookCommandHandler>();
        services.AddScoped<IBookCommandHandler>(sp => sp.GetRequiredService<BookCommandHandler>());

        services.AddScoped<IBookContentPersister, BookContentPersister>();
        services.AddScoped<BookReadingService>();
        services.AddScoped<ProjectUseCases>();
        services.AddScoped<BookUseCases>();
        services.AddScoped<EnqueueUseCases>();
        services.AddScoped<BookHierarchyLoader>();
        services.AddScoped<IBookProjectLoader, BookProjectLoader>();
        services.AddScoped<ISelectionCoordinator, BookSelectionCoordinator>();
        services.AddScoped<BookHierarchyPresenter>();
        
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
        // App-scoped: one queue runs at a time on one GPU, so every circuit should read the same
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
        services.AddScoped<IAudioQueueProcessor, AudioQueueProcessor>();
        services.AddScoped<IQueueProcessor<Read2Me.Services.Audio.QueuedAudioItem>>(
            sp => sp.GetRequiredService<IAudioQueueProcessor>());
        services.AddSingleton<IProcessingGate<Read2Me.Services.Audio.QueuedAudioItem>, ProcessingGate<Read2Me.Services.Audio.QueuedAudioItem>>();
        services.AddHostedService<QueueWorker<Read2Me.Services.Audio.QueuedAudioItem>>();
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
        services.AddScoped<IFfmpegProber, FfmpegProber>();
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
        services.AddScoped<Read2Me.Core.Audio.IAudioPipeline, FileAudioPipeline>();
        services.AddScoped<IAudioItemPipeline, AudioItemPipeline>();
        services.AddScoped<IAudioItemResolver, AudioItemResolver>();
        services.AddScoped<IAudioResultRecorder, AudioResultRecorder>();
        services.AddScoped<VoiceDesignPromptService>();
        services.AddScoped<IParagraphTtsClientResolver, ParagraphTtsClientResolver>();
        services.AddKeyedScoped<IParagraphTtsClient, VoxCpm2ParagraphTtsClient>(Read2Me.AppData.Entities.ParagraphTtsServiceType.VoxCpm2);
        services.AddKeyedScoped<IParagraphTtsClient, ChatterboxParagraphTtsClient>(Read2Me.AppData.Entities.ParagraphTtsServiceType.Chatterbox);
        services.AddKeyedScoped<IParagraphTtsClient, ChatterboxTurboParagraphTtsClient>(Read2Me.AppData.Entities.ParagraphTtsServiceType.ChatterboxTurbo);
        services.AddKeyedScoped<IParagraphTtsClient, Qwen3ParagraphTtsClient>(Read2Me.AppData.Entities.ParagraphTtsServiceType.Qwen3Base);
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
        services.AddSingleton<NodeStatusService>();
        services.AddSingleton<CharacterQueueService>();
        services.AddSingleton<Read2Me.App.State.AttributionProgressState>();
        services.AddSingleton<IQueueSource<QueuedParagraph>>(
            sp => sp.GetRequiredService<CharacterQueueService>());
        services.AddScoped<ICharacterQueueProcessor, CharacterQueueProcessor>();
        services.AddScoped<IQueueProcessor<QueuedParagraph>>(
            sp => sp.GetRequiredService<ICharacterQueueProcessor>());
        services.AddSingleton<IProcessingGate<QueuedParagraph>, ProcessingGate<QueuedParagraph>>();
        services.AddHostedService<QueueWorker<QueuedParagraph>>();
        services.AddScoped<CharacterAttributionService>();
        services.AddScoped<IChainStep>(sp => sp.GetRequiredService<CharacterAttributionService>());
        services.AddScoped<AttributionEscalationChain>();
        services.AddScoped<CharacterResolver>();
        services.AddScoped<Read2Me.App.Services.VoiceOrchestrator>();
        services.AddScoped<CharacterPresenter>();
        services.AddScoped<Read2Me.App.State.VoicePromptGenerationState>();
        services.AddSingleton<EventBroadcaster<VoiceBatchEvent>>();
        services.AddSingleton<VoiceBatchRunner>();

        // AI book edits
        services.AddScoped<Read2Me.Services.Llm.ChapterOutlineBuilder>();
        services.AddScoped<Read2Me.Services.BookEdits.ScopeResolver>();
        services.AddScoped<Read2Me.Services.BookEdits.BookEditPlanner>();
        services.AddScoped<Read2Me.Services.BookEdits.BookEditProposalService>();

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
        // Pre-flight is scoped: it shows a dialog, and IDialogService lives per circuit.
        services.AddScoped<IAiTaskRequirementsResolver, AiTaskRequirementsResolver>();
        services.AddScoped<IAiPreflight, AiPreflight>();
        return services;
    }

    // Maps each registered service to the gate(s) recovery must hold: llama gates the paragraph
    // (character-attribution) queue; every TTS/whisper/similarity service gates the audio queue.
    private static WatchdogGateMap BuildGateMap(System.IServiceProvider sp)
    {
        var registry = sp.GetRequiredService<DockerAiServiceRegistry>();
        IWatchdogGate paragraphGate = new ProcessingGateAdapter<QueuedParagraph>(
            sp.GetRequiredService<IProcessingGate<QueuedParagraph>>(),
            sp.GetRequiredService<IQueueSource<QueuedParagraph>>());
        IWatchdogGate audioGate = new ProcessingGateAdapter<Read2Me.Services.Audio.QueuedAudioItem>(
            sp.GetRequiredService<IProcessingGate<Read2Me.Services.Audio.QueuedAudioItem>>(),
            sp.GetRequiredService<IQueueSource<Read2Me.Services.Audio.QueuedAudioItem>>());

        var map = new System.Collections.Generic.Dictionary<string, System.Collections.Generic.IReadOnlyList<IWatchdogGate>>(
            System.StringComparer.OrdinalIgnoreCase);
        foreach (var svc in registry.All)
        {
            map[svc.Name] = svc.Name.Equals("llama", System.StringComparison.OrdinalIgnoreCase)
                ? new[] { paragraphGate }
                : new[] { audioGate };
        }
        return new WatchdogGateMap(map);
    }

    public static IServiceCollection AddAppState(this IServiceCollection services)
    {
        services.AddSingleton<ThemeService>();
        services.AddScoped<BookTreeState>();
        services.AddScoped<BookSelectionState>();
        services.AddScoped<AudioItemSelectionState>();
        services.AddScoped<MenuActions>();
        return services;
    }

    public static IServiceCollection AddAppDatabase(this IServiceCollection services)
    {
        services.AddDbContextFactory<Read2MeDbContext>((sp, options) =>
        {
            var workspace = sp.GetRequiredService<IOptions<WorkspaceOptions>>().Value;
            var dbPath = Path.Combine(workspace.FolderPath, "app.db");
            options.UseSqlite($"Data Source={dbPath}");
        });
        return services;
    }
}
