using System.IO;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Read2Me.App.Audio;
using Read2Me.App.Characters;
using Read2Me.App.Queueing;
using Read2Me.App.State;
using Read2Me.Services.Queueing;
using Read2Me.Core.Configuration;
using Read2Me.Core.IO;
using Read2Me.App.Shared.BookMenus;
using Read2Me.AppData;
using Read2Me.Data;
using Read2Me.Services;
using Read2Me.Services.Audio;
using Read2Me.Services.Audio.ParagraphTts;
using Read2Me.Services.Audio.Transcription;
using Read2Me.Services.Audio.VoiceDesign;
using Read2Me.Services.Books;
using Read2Me.Services.Characters;
using Read2Me.Services.IO;
using Read2Me.Services.NodeStatus;
using Read2Me.Services.Llm;
using Read2Me.Services.UseCases;
using Read2Me.Services.Voice;
using MudBlazor.Services;

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
        services.AddBookCommandHandlers();
        
        services.AddScoped<BookCommandHandler>();
        services.AddScoped<IBookCommandHandler>(sp => sp.GetRequiredService<BookCommandHandler>());

        services.AddScoped<IBookContentPersister, BookContentPersister>();
        services.AddScoped<BookReadingService>();
        services.AddScoped<ProjectUseCases>();
        services.AddScoped<BookUseCases>();
        services.AddScoped<BookHierarchyLoader>();
        services.AddScoped<BookHierarchyPresenter>();
        
        services.AddSingleton<EpubFileReader>();
        services.AddSingleton<TextFileReader>();

        return services;
    }

    public static IServiceCollection AddLlmServices(this IServiceCollection services)
    {
        services.AddScoped<LlmSettingsService>();
        services.AddScoped<LlmPromptService>();
        services.AddScoped<ILlmClient, OpenAiLlmClient>();
        services.AddSingleton<LlmStreamBroadcaster>();
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
        services.AddHostedService<QueueWorker<Read2Me.Services.Audio.QueuedAudioItem>>();
        return services;
    }

    public static IServiceCollection AddAudioServices(this IServiceCollection services)
    {
        services.AddScoped<VoiceDesignSettingsService>();
        services.AddScoped<TranscriptionSettingsService>();
        services.AddScoped<ParagraphTtsSettingsService>();
        services.AddScoped<IFfmpegProber, FfmpegProber>();
        services.AddScoped<AudioProcessingSettingsService>();
        services.AddSingleton<IWerComparer, WerComparer>();
        services.AddSingleton<Read2Me.Services.Audio.AudioReviewService>();
        services.AddSingleton<Read2Me.Services.Audio.AudioGenBroadcaster>();
        services.AddScoped<IAudioNormalizer, FfmpegAudioNormalizer>();
        services.AddScoped<ITranscriptionClientResolver, TranscriptionClientResolver>();
        services.AddKeyedScoped<ITranscriptionClient, WhisperTranscriptionClient>(Read2Me.AppData.Entities.TranscriptionServiceType.LocalWhisper);
        services.AddScoped<IVoiceDesignClientResolver, VoiceDesignClientResolver>();
        services.AddScoped<VoiceAudioGenerator>();
        services.AddScoped<IVoiceAudioGenerator>(sp => sp.GetRequiredService<VoiceAudioGenerator>());
        services.AddKeyedScoped<IVoiceDesignClient, VoxCpm2VoiceDesignClient>(Read2Me.AppData.Entities.VoiceDesignServiceType.VoxCpm2);
        services.AddKeyedScoped<IVoiceDesignClient, Qwen3VoiceDesignClient>(Read2Me.AppData.Entities.VoiceDesignServiceType.Qwen3);
        services.AddScoped<Read2Me.Core.Audio.IAudioPipeline, FileAudioPipeline>();
        services.AddScoped<VoiceDesignPromptService>();
        services.AddScoped<IParagraphTtsClientResolver, ParagraphTtsClientResolver>();
        services.AddKeyedScoped<IParagraphTtsClient, VoxCpm2ParagraphTtsClient>(Read2Me.AppData.Entities.ParagraphTtsServiceType.VoxCpm2);
        return services;
    }

    public static IServiceCollection AddCharacterServices(this IServiceCollection services)
    {
        services.AddSingleton<NodeStatusService>();
        services.AddSingleton<CharacterQueueService>();
        services.AddSingleton<IQueueSource<QueuedParagraph>>(
            sp => sp.GetRequiredService<CharacterQueueService>());
        services.AddScoped<ICharacterQueueProcessor, CharacterQueueProcessor>();
        services.AddScoped<IQueueProcessor<QueuedParagraph>>(
            sp => sp.GetRequiredService<ICharacterQueueProcessor>());
        services.AddHostedService<QueueWorker<QueuedParagraph>>();
        services.AddScoped<CharacterAttributionService>();
        services.AddScoped<CharacterResolver>();
        services.AddScoped<Read2Me.App.Services.VoiceOrchestrator>();
        services.AddScoped<CharacterPresenter>();
        return services;
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
