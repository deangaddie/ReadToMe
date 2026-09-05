using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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
        // The Voice audio writer removes a stale original when a voice stops naming its audio.
        services.TryAddScoped<Read2Me.Services.Audio.IVoiceOriginalStore, Read2Me.Services.Audio.VoiceOriginalStore>();
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
        services.AddScoped<ICommandHandler<SetParagraphsCharacterCommand>, SetParagraphsCharacterHandler>();
        services.AddScoped<ICommandHandler<AttributeItemsCommand>, AttributeItemsHandler>();
        services.AddScoped<ICommandHandler<AddCharacterAliasCommand>, AddCharacterAliasHandler>();
        services.AddScoped<ICommandHandler<RemoveCharacterAliasCommand>, RemoveCharacterAliasHandler>();
        services.AddScoped<ICommandHandler<MergeCharactersCommand>, MergeCharactersHandler>();
        services.AddScoped<ICommandHandler<DeleteCharacterCommand>, DeleteCharacterHandler>();
        services.AddScoped<ICommandHandler<RenameCharacterCommand>, RenameCharacterHandler>();

        // Narrator
        services.AddScoped<ICommandHandler<SetNarratorCharacterCommand>, SetNarratorCharacterHandler>();

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

        // Item insertion
        services.AddScoped<ICommandHandler<InsertParagraphItemCommand>, InsertParagraphItemHandler>();

        // Pause
        services.AddScoped<ICommandHandler<AddPausesCommand>, AddPausesHandler>();
        services.AddScoped<ICommandHandler<InsertPauseParagraphCommand>, InsertPauseParagraphHandler>();

        // Clear
        services.AddScoped<ICommandHandler<ClearBookContentCommand>, ClearBookContentHandler>();

        // AI book edits
        services.AddScoped<ICommandHandler<ApplyBookEditsCommand>, ApplyBookEditsHandler>();

        // Audio
        services.AddScoped<ICommandHandler<SetParagraphItemAudioCommand>, SetParagraphItemAudioHandler>();
        services.AddScoped<ICommandHandler<SetAudioReviewCommand>, SetAudioReviewHandler>();
        services.AddScoped<ICommandHandler<DismissAudioReviewCommand>, DismissAudioReviewHandler>();

        services.AddScoped<ProjectReader>();
        services.AddScoped<IProjectReader>(sp => sp.GetRequiredService<ProjectReader>());
        services.AddScoped<IProjectCatalogReader>(sp => sp.GetRequiredService<ProjectReader>());
        services.AddScoped<IBookContentReader>(sp => sp.GetRequiredService<ProjectReader>());
        services.AddScoped<ICharacterReader>(sp => sp.GetRequiredService<ProjectReader>());
        services.AddScoped<IUnattributedItemCounter>(sp => sp.GetRequiredService<ProjectReader>());
        services.AddScoped<IAudioItemReader>(sp => sp.GetRequiredService<ProjectReader>());

        // ── Book mutations (ADR 0007) ────────────────────────────────────────
        // The write-side spine. Singletons because serialization and revision order are
        // process-wide facts about a project, not per-circuit ones.
        services.TryAddSingleton<Mutations.ProjectWriteLocks>();
        services.TryAddSingleton<Mutations.BookRevisionSequence>();
        services.AddOptions<Mutations.BookMutationOptions>();
        services.TryAddSingleton<Events.EventBroadcaster<Mutations.BookMutationReceipt>>();
        services.AddScoped<Mutations.BookMutations>();
        services.AddScoped<
            Mutations.IBookMutationImplementation<Mutations.InsertParagraphItemMutation>,
            Mutations.Implementations.InsertParagraphItemMutationImplementation>();
        // Additive structural mutations
        services.AddScoped<
            Mutations.IBookMutationImplementation<Mutations.SplitAtPartMutation>,
            Mutations.Implementations.SplitAtPartMutationImplementation>();
        services.AddScoped<
            Mutations.IBookMutationImplementation<Mutations.SplitAtChapterMutation>,
            Mutations.Implementations.SplitAtChapterMutationImplementation>();
        services.AddScoped<
            Mutations.IBookMutationImplementation<Mutations.SplitAtParagraphMutation>,
            Mutations.Implementations.SplitAtParagraphMutationImplementation>();
        services.AddScoped<
            Mutations.IBookMutationImplementation<Mutations.SplitAtItemMutation>,
            Mutations.Implementations.SplitAtItemMutationImplementation>();
        services.AddScoped<
            Mutations.IBookMutationImplementation<Mutations.AddBookTitleMutation>,
            Mutations.Implementations.AddBookTitleMutationImplementation>();
        services.AddScoped<
            Mutations.IBookMutationImplementation<Mutations.AddVolumeTitlesMutation>,
            Mutations.Implementations.AddVolumeTitlesMutationImplementation>();
        services.AddScoped<
            Mutations.IBookMutationImplementation<Mutations.AddPartTitlesMutation>,
            Mutations.Implementations.AddPartTitlesMutationImplementation>();
        services.AddScoped<
            Mutations.IBookMutationImplementation<Mutations.AddChapterTitlesMutation>,
            Mutations.Implementations.AddChapterTitlesMutationImplementation>();
        services.AddScoped<
            Mutations.IBookMutationImplementation<Mutations.AddPausesMutation>,
            Mutations.Implementations.AddPausesMutationImplementation>();
        services.AddScoped<
            Mutations.IBookMutationImplementation<Mutations.InsertPauseParagraphMutation>,
            Mutations.Implementations.InsertPauseParagraphMutationImplementation>();
        // Destructive structural mutations
        services.AddScoped<
            Mutations.IBookMutationImplementation<Mutations.MergeVolumeMutation>,
            Mutations.Implementations.MergeVolumeMutationImplementation>();
        services.AddScoped<
            Mutations.IBookMutationImplementation<Mutations.MergePartMutation>,
            Mutations.Implementations.MergePartMutationImplementation>();
        services.AddScoped<
            Mutations.IBookMutationImplementation<Mutations.MergeChapterMutation>,
            Mutations.Implementations.MergeChapterMutationImplementation>();
        services.AddScoped<
            Mutations.IBookMutationImplementation<Mutations.MergeParagraphMutation>,
            Mutations.Implementations.MergeParagraphMutationImplementation>();
        services.AddScoped<
            Mutations.IBookMutationImplementation<Mutations.MergeParagraphItemMutation>,
            Mutations.Implementations.MergeParagraphItemMutationImplementation>();
        services.AddScoped<
            Mutations.IBookMutationImplementation<Mutations.DeleteVolumeMutation>,
            Mutations.Implementations.DeleteVolumeMutationImplementation>();
        services.AddScoped<
            Mutations.IBookMutationImplementation<Mutations.DeletePartMutation>,
            Mutations.Implementations.DeletePartMutationImplementation>();
        services.AddScoped<
            Mutations.IBookMutationImplementation<Mutations.DeleteChapterMutation>,
            Mutations.Implementations.DeleteChapterMutationImplementation>();
        services.AddScoped<
            Mutations.IBookMutationImplementation<Mutations.DeleteParagraphMutation>,
            Mutations.Implementations.DeleteParagraphMutationImplementation>();
        services.AddScoped<
            Mutations.IBookMutationImplementation<Mutations.DeleteParagraphItemMutation>,
            Mutations.Implementations.DeleteParagraphItemMutationImplementation>();
        services.AddScoped<
            Mutations.IBookMutationImplementation<Mutations.ClearBookContentMutation>,
            Mutations.Implementations.ClearBookContentMutationImplementation>();
        // Speaker attribution — the exact, high-frequency family
        services.AddScoped<
            Mutations.IBookMutationImplementation<Mutations.SetItemSpeakerMutation>,
            Mutations.Implementations.SetItemSpeakerMutationImplementation>();
        services.AddScoped<
            Mutations.IBookMutationImplementation<Mutations.SetParagraphSpeakerMutation>,
            Mutations.Implementations.SetParagraphSpeakerMutationImplementation>();
        services.AddScoped<
            Mutations.IBookMutationImplementation<Mutations.SetParagraphsSpeakerMutation>,
            Mutations.Implementations.SetParagraphsSpeakerMutationImplementation>();
        services.AddScoped<
            Mutations.IBookMutationImplementation<Mutations.AttributeParagraphItemsMutation>,
            Mutations.Implementations.AttributeParagraphItemsMutationImplementation>();
        // Audio assignment and reviews — the Audio Queue's exact, high-frequency family
        services.AddScoped<
            Mutations.IBookMutationImplementation<Mutations.RecordParagraphItemAudioMutation>,
            Mutations.Implementations.RecordParagraphItemAudioMutationImplementation>();
        services.AddScoped<
            Mutations.IBookMutationImplementation<Mutations.SetParagraphItemAudioMutation>,
            Mutations.Implementations.SetParagraphItemAudioMutationImplementation>();
        services.AddScoped<
            Mutations.IBookMutationImplementation<Mutations.SetAudioReviewMutation>,
            Mutations.Implementations.SetAudioReviewMutationImplementation>();
        services.AddScoped<
            Mutations.IBookMutationImplementation<Mutations.DismissAudioReviewMutation>,
            Mutations.Implementations.DismissAudioReviewMutationImplementation>();
        // Character, narrator and policy lifecycles — Book-wide by reach, however small the write
        services.AddScoped<
            Mutations.IBookMutationImplementation<Mutations.CreateCharacterMutation>,
            Mutations.Implementations.CreateCharacterMutationImplementation>();
        services.AddScoped<
            Mutations.IBookMutationImplementation<Mutations.RenameCharacterMutation>,
            Mutations.Implementations.RenameCharacterMutationImplementation>();
        services.AddScoped<
            Mutations.IBookMutationImplementation<Mutations.AddCharacterAliasMutation>,
            Mutations.Implementations.AddCharacterAliasMutationImplementation>();
        services.AddScoped<
            Mutations.IBookMutationImplementation<Mutations.RemoveCharacterAliasMutation>,
            Mutations.Implementations.RemoveCharacterAliasMutationImplementation>();
        services.AddScoped<
            Mutations.IBookMutationImplementation<Mutations.MergeCharactersMutation>,
            Mutations.Implementations.MergeCharactersMutationImplementation>();
        services.AddScoped<
            Mutations.IBookMutationImplementation<Mutations.DeleteCharacterMutation>,
            Mutations.Implementations.DeleteCharacterMutationImplementation>();
        services.AddScoped<
            Mutations.IBookMutationImplementation<Mutations.SetNarratorCharacterMutation>,
            Mutations.Implementations.SetNarratorCharacterMutationImplementation>();
        services.AddScoped<
            Mutations.IBookMutationImplementation<Mutations.SetNarratorOnlyModeMutation>,
            Mutations.Implementations.SetNarratorOnlyModeMutationImplementation>();
        // Voice and Voice Rule lifecycles — Voice rows and rule rows only, previews everywhere
        services.AddScoped<
            Mutations.IBookMutationImplementation<Mutations.CreateVoiceMutation>,
            Mutations.Implementations.CreateVoiceMutationImplementation>();
        services.AddScoped<
            Mutations.IBookMutationImplementation<Mutations.SetVoiceDefaultMutation>,
            Mutations.Implementations.SetVoiceDefaultMutationImplementation>();
        services.AddScoped<
            Mutations.IBookMutationImplementation<Mutations.UpdateVoiceMutation>,
            Mutations.Implementations.UpdateVoiceMutationImplementation>();
        services.AddScoped<
            Mutations.IBookMutationImplementation<Mutations.SetVoiceDesignPromptMutation>,
            Mutations.Implementations.SetVoiceDesignPromptMutationImplementation>();
        services.AddScoped<
            Mutations.IBookMutationImplementation<Mutations.SetVoiceDesignSettingsOverrideMutation>,
            Mutations.Implementations.SetVoiceDesignSettingsOverrideMutationImplementation>();
        services.AddScoped<
            Mutations.IBookMutationImplementation<Mutations.SetVoiceTtsSettingsOverrideMutation>,
            Mutations.Implementations.SetVoiceTtsSettingsOverrideMutationImplementation>();
        services.AddScoped<
            Mutations.IBookMutationImplementation<Mutations.SetVoiceTranscriptMutation>,
            Mutations.Implementations.SetVoiceTranscriptMutationImplementation>();
        services.AddScoped<
            Mutations.IBookMutationImplementation<Mutations.SetVoiceAudioMutation>,
            Mutations.Implementations.SetVoiceAudioMutationImplementation>();
        services.AddScoped<
            Mutations.IBookMutationImplementation<Mutations.SetVoiceGeneratedMutation>,
            Mutations.Implementations.SetVoiceGeneratedMutationImplementation>();
        services.AddScoped<
            Mutations.IBookMutationImplementation<Mutations.SetVoiceSourceMutation>,
            Mutations.Implementations.SetVoiceSourceMutationImplementation>();
        services.AddScoped<
            Mutations.IBookMutationImplementation<Mutations.DeleteVoiceMutation>,
            Mutations.Implementations.DeleteVoiceMutationImplementation>();
        services.AddScoped<
            Mutations.IBookMutationImplementation<Mutations.CreateVoiceRuleMutation>,
            Mutations.Implementations.CreateVoiceRuleMutationImplementation>();
        services.AddScoped<
            Mutations.IBookMutationImplementation<Mutations.DeleteVoiceRuleMutation>,
            Mutations.Implementations.DeleteVoiceRuleMutationImplementation>();
        services.AddScoped<
            Mutations.IBookMutationImplementation<Mutations.MoveVoiceRuleMutation>,
            Mutations.Implementations.MoveVoiceRuleMutationImplementation>();
        // Imports and rereads — clearing and repopulating as one commit, never two.
        services.TryAddScoped<IBookContentPersister, BookContentPersister>();
        services.AddScoped<
            Mutations.IBookMutationImplementation<Mutations.ImportBookContentMutation>,
            Mutations.Implementations.ImportBookContentMutationImplementation>();
        // Manual and AI book edits — node titles and item text, and the stale audio that goes with
        // a rewrite.
        services.AddScoped<
            Mutations.IBookMutationImplementation<Mutations.UpdateVolumeTitleMutation>,
            Mutations.Implementations.UpdateVolumeTitleMutationImplementation>();
        services.AddScoped<
            Mutations.IBookMutationImplementation<Mutations.UpdatePartTitleMutation>,
            Mutations.Implementations.UpdatePartTitleMutationImplementation>();
        services.AddScoped<
            Mutations.IBookMutationImplementation<Mutations.UpdateChapterTitleMutation>,
            Mutations.Implementations.UpdateChapterTitleMutationImplementation>();
        services.AddScoped<
            Mutations.IBookMutationImplementation<Mutations.UpdateParagraphItemTextMutation>,
            Mutations.Implementations.UpdateParagraphItemTextMutationImplementation>();
        services.AddScoped<
            Mutations.IBookMutationImplementation<Mutations.ApplyBookEditsMutation>,
            Mutations.Implementations.ApplyBookEditsMutationImplementation>();
        // Removing a Voice takes its audio with it, after the commit. The arriving half needs the
        // audio pipeline and so is registered by the application.
        services.TryAddScoped<Audio.IVoiceAudioRemover, Audio.VoiceAudioRemover>();
        // The roster's read-plus-write seam, registered here because CreateCharacterHandler needs it.
        services.TryAddScoped<Characters.CharacterResolver>();
        services.AddScoped<BookCommandHandler>();
        services.AddScoped<IBookCommandHandler>(sp => sp.GetRequiredService<BookCommandHandler>());

        return services;
    }
}
