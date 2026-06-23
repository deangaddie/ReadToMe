using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Read2Me.Core.IO;
using Read2Me.Core.Models;
using Read2Me.Services.Commands;
using Read2Me.Services.Commands.Handlers;
using Read2Me.Services.IO;

namespace Read2Me.Services;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddBookCommandHandlers(this IServiceCollection services)
    {
        services.AddLogging();
        services.AddSingleton<IFileSystem, FileSystemService>();
        // Delete
        services.AddScoped<ICommandHandler<DeleteVolumeCommand>, DeleteVolumeHandler>();
        services.AddScoped<ICommandHandler<DeletePartCommand>, DeletePartHandler>();
        services.AddScoped<ICommandHandler<DeleteChapterCommand>, DeleteChapterHandler>();
        services.AddScoped<ICommandHandler<DeleteParagraphCommand>, DeleteParagraphHandler>();
        services.AddScoped<ICommandHandler<DeleteParagraphItemCommand>, DeleteParagraphItemHandler>();

        // Project/Hierarchy related dependencies
        services.AddScoped<ProjectDbSession>();

        // Update
        services.AddScoped<ICommandHandler<UpdateVolumeTitleCommand>, UpdateVolumeTitleHandler>();
        services.AddScoped<ICommandHandler<UpdatePartTitleCommand>, UpdatePartTitleHandler>();
        services.AddScoped<ICommandHandler<UpdateChapterTitleCommand>, UpdateChapterTitleHandler>();
        services.AddScoped<ICommandHandler<UpdateParagraphItemTextCommand>, UpdateParagraphItemTextHandler>();

        // Split
        services.AddScoped<ICommandHandler<SplitAtPartCommand>, SplitAtPartHandler>();
        services.AddScoped<ICommandHandler<SplitAtChapterCommand>, SplitAtChapterHandler>();
        services.AddScoped<ICommandHandler<SplitAtParagraphCommand>, SplitAtParagraphHandler>();
        services.AddScoped<ICommandHandler<SplitAtItemCommand>, SplitAtItemHandler>();

        // Merge
        services.AddScoped<ICommandHandler<MergeVolumeCommand>, MergeVolumeHandler>();
        services.AddScoped<ICommandHandler<MergePartCommand>, MergePartHandler>();
        services.AddScoped<ICommandHandler<MergeChapterCommand>, MergeChapterHandler>();
        services.AddScoped<ICommandHandler<MergeParagraphCommand>, MergeParagraphHandler>();
        services.AddScoped<ICommandHandler<MergeParagraphItemCommand>, MergeParagraphItemHandler>();

        // Character
        services.AddScoped<ICommandHandler<SetItemCharacterCommand>, SetItemCharacterHandler>();
        services.AddScoped<ICommandHandler<CreateCharacterCommand>, CreateCharacterHandler>();
        services.AddScoped<ICommandHandler<SetParagraphCharacterCommand>, SetParagraphCharacterHandler>();
        services.AddScoped<ICommandHandler<AddCharacterAliasCommand>, AddCharacterAliasHandler>();
        services.AddScoped<ICommandHandler<RemoveCharacterAliasCommand>, RemoveCharacterAliasHandler>();
        services.AddScoped<ICommandHandler<MergeCharactersCommand>, MergeCharactersHandler>();
        services.AddScoped<ICommandHandler<DeleteCharacterCommand>, DeleteCharacterHandler>();

        // Voice
        services.AddScoped<ICommandHandler<CreateVoiceCommand>, CreateVoiceHandler>();
        services.AddScoped<ICommandHandler<SetVoiceDefaultCommand>, SetVoiceDefaultHandler>();
        services.AddScoped<ICommandHandler<UpdateVoiceCommand>, UpdateVoiceHandler>();
        services.AddScoped<ICommandHandler<SetVoiceDesignPromptCommand>, SetVoiceDesignPromptHandler>();
        services.AddScoped<ICommandHandler<SetVoiceSettingsOverrideCommand>, SetVoiceSettingsOverrideHandler>();
        services.AddScoped<ICommandHandler<SetVoiceTtsSettingsOverrideCommand>, SetVoiceTtsSettingsOverrideHandler>();
        services.AddScoped<ICommandHandler<SetVoiceTranscriptCommand>, SetVoiceTranscriptHandler>();
        services.AddScoped<ICommandHandler<SetVoiceAudioCommand>, SetVoiceAudioHandler>();
        services.AddScoped<ICommandHandler<SetVoiceGeneratedCommand>, SetVoiceGeneratedHandler>();
        services.AddScoped<ICommandHandler<SetVoiceSourceCommand>, SetVoiceSourceHandler>();
        services.AddScoped<ICommandHandler<DeleteVoiceCommand>, DeleteVoiceHandler>();
        services.AddScoped<ICommandHandler<CreateVoiceRuleCommand>, CreateVoiceRuleHandler>();
        services.AddScoped<ICommandHandler<DeleteVoiceRuleCommand>, DeleteVoiceRuleHandler>();
        services.AddScoped<ICommandHandler<MoveVoiceRuleCommand>, MoveVoiceRuleHandler>();

        // Title
        services.AddScoped<ICommandHandler<AddBookTitleCommand>, AddBookTitleHandler>();
        services.AddScoped<ICommandHandler<AddVolumeTitlesCommand>, AddVolumeTitlesHandler>();
        services.AddScoped<ICommandHandler<AddPartTitlesCommand>, AddPartTitlesHandler>();
        services.AddScoped<ICommandHandler<AddChapterTitlesCommand>, AddChapterTitlesHandler>();

        // Pause
        services.AddScoped<ICommandHandler<AddPausesCommand>, AddPausesHandler>();
        services.AddScoped<ICommandHandler<InsertPauseParagraphCommand>, InsertPauseParagraphHandler>();

        // Clear
        services.AddScoped<ICommandHandler<ClearBookContentCommand>, ClearBookContentHandler>();

        // Audio
        services.AddScoped<ICommandHandler<SetParagraphItemAudioCommand>, SetParagraphItemAudioHandler>();
        services.AddScoped<ICommandHandler<SetAudioReviewCommand>, SetAudioReviewHandler>();
        services.AddScoped<ICommandHandler<DismissAudioReviewCommand>, DismissAudioReviewHandler>();

        services.AddScoped<ProjectReader>();
        services.AddScoped<IProjectReader>(sp => sp.GetRequiredService<ProjectReader>());
        services.AddScoped<BookCommandHandler>();
        services.AddScoped<IBookCommandHandler>(sp => sp.GetRequiredService<BookCommandHandler>());

        return services;
    }
}
