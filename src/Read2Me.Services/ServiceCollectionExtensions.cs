using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Read2Me.Core.IO;
using Read2Me.Core.Models;
using Read2Me.Services.Commands;
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
        // The command table's Handler rows: the classes for the commands with logic of their own.
        BookCommandTable.RegisterHandlers(services);

        // Project/Hierarchy related dependencies
        services.AddScoped<ProjectDbSession>();

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
