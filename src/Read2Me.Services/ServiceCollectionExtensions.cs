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
        services.AddScoped<ICommandHandler<SetItemDeliveryCueCommand>, SetItemDeliveryCueHandler>();

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
        services.AddScoped<ICommandHandler<CreateCharacterAndAssignCommand>, CreateCharacterAndAssignHandler>();
        services.AddScoped<ICommandHandler<SetParagraphCharacterCommand>, SetParagraphCharacterHandler>();
        services.AddScoped<ICommandHandler<SetParagraphsCharacterCommand>, SetParagraphsCharacterHandler>();
        services.AddScoped<ICommandHandler<ReleaseNarrationCommand>, ReleaseNarrationHandler>();
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
        services.AddScoped<ICommandHandler<AddTitlesCommand>, AddTitlesHandler>();

        // Item insertion
        services.AddScoped<ICommandHandler<InsertParagraphItemCommand>, InsertParagraphItemHandler>();
        services.AddScoped<ICommandHandler<InsertNodeCommand>, InsertNodeHandler>();

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
        // process-wide facts about a project, not per-request ones.
        services.TryAddSingleton<Mutations.ProjectWriteLocks>();
        services.TryAddSingleton<Mutations.BookRevisionSequence>();
        // When a checkpoint was confirmed or skipped; tests swap in a manual clock.
        services.TryAddSingleton(TimeProvider.System);
        // Process-wide settings-change signal for the live hub relay (the settings services are scoped).
        services.TryAddSingleton<Events.EventBroadcaster<Events.SettingsChanged>>();
        // Series registries (ADR 0018): no Book mutation and no History; one lock across them all.
        services.TryAddSingleton<Events.EventBroadcaster<Events.SeriesChanged>>();
        services.TryAddSingleton<Series.SeriesRegistryLock>();
        services.TryAddSingleton<Series.VoiceAudioHashes>();
        // Every series read (#247): the snapshot of a series, read with no lock and no healing (#234).
        services.AddScoped<Series.SeriesSnapshotLoader>();
        // Every series.db write: takes the lock, heals, plans from a snapshot.
        services.AddScoped<Series.SeriesRegistry>();
        // The only writer of Voices into peer books (link gains, push/take, copy after commit).
        services.AddScoped<Series.SeriesVoiceReplicator>();
        // Shared Voice edits (ADR 0018): copied into the other linked books after each commit.
        services.TryAddEnumerable(ServiceDescriptor.Scoped<Mutations.IBookCommitHook, Series.SeriesVoiceCopier>());
        services.AddOptions<Mutations.BookMutationOptions>();
        services.TryAddSingleton<Events.EventBroadcaster<Mutations.BookMutationReceipt>>();
        services.AddScoped<Mutations.BookMutations>();
        // History (ADR 0016): every state mutation commits a step through BookMutations.
        services.TryAddSingleton<Events.EventBroadcaster<History.HistoryStepEvent>>();
        services.AddOptions<History.HistoryOptions>();
        services.TryAddSingleton<History.ProjectHistory>();
        services.TryAddSingleton<History.BackgroundRuns>();
        services.AddScoped<Mutations.MutationOrigin>();
        services.AddScoped<History.HistoryStepScope>();
        // Project activity: busy, restoring and deleting together; each busy source registers with it when built.
        services.TryAddSingleton<History.ProjectActivity>();
        // Restore: the run and its started/finished signal; the restoring flag lives in the gate.
        services.TryAddSingleton<Events.EventBroadcaster<History.RestoreEvent>>();
        services.AddScoped<History.HistoryRestore>();
        // Process-wide, not request-scoped: the HTTP API plans in one request and proposes/applies
        // in others, and a Restore drops a project's sessions.
        services.TryAddSingleton<BookEdits.IBookEditSessionStore>(_ => new BookEdits.BookEditSessionStore(TimeProvider.System));
        AddBookMutationImplementations(services);
        services.AddScoped<Pronunciations.PronunciationTryOut>();
        // Imports and rereads clear and repopulate as one commit, never two.
        services.TryAddScoped<IBookContentPersister, BookContentPersister>();
        // Removing a Voice takes its audio with it, after the commit. The arriving half needs the
        // audio pipeline and so is registered by the application.
        services.TryAddScoped<Audio.IVoiceAudioRemover, Audio.VoiceAudioRemover>();
        // The roster's read-plus-write seam, registered here because CreateCharacterHandler needs it.
        services.TryAddScoped<Characters.CharacterResolver>();
        services.AddScoped<BookCommandDispatcher>();

        return services;
    }

    /// <summary>
    /// Registers every closed <see cref="Mutations.IBookMutationImplementation{TMutation}"/> in this
    /// assembly, scoped, so a new Book mutation needs no registration line. A test that registers a
    /// fake after <see cref="AddBookCommandHandlers"/> still wins: the last registration resolves.
    /// </summary>
    private static void AddBookMutationImplementations(IServiceCollection services)
    {
        var contract = typeof(Mutations.IBookMutationImplementation<>);
        var registrations = typeof(ServiceCollectionExtensions).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false, IsGenericTypeDefinition: false })
            .SelectMany(t => t.GetInterfaces()
                .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == contract)
                .Select(i => (Service: i, Implementation: t)));
        foreach (var (service, implementation) in registrations)
            services.AddScoped(service, implementation);
    }
}
