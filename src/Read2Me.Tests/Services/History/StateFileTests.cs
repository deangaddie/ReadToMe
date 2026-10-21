using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Read2Me.Core.Configuration;
using Read2Me.Core.IO;
using Read2Me.Core.Models;
using Read2Me.Data;
using Read2Me.Services;
using Read2Me.Services.Audio;
using Read2Me.Services.History;
using Read2Me.Services.Mutations;
using Read2Me.TestUtils;
using Read2Me.Tests.Infrastructure;
using Xunit;

namespace Read2Me.Tests.Services.History;

/// <summary>
/// The files that are part of Project state — voice reference audio and the editor's original, the
/// cover, the source book file — are recorded with each History step and read back as they were
/// (ADR 0016, "Files"). Driven through real mutations on a migrated temp project.
/// </summary>
public class StateFileTests : ProjectDbTestBase
{
    private readonly ProjectFolderId _folder;
    private ServiceProvider _root;

    public StateFileTests()
    {
        _folder = new ProjectFolderId(FolderName);
        _root = NewApp(snapshotEvery: 50);
    }

    private ProjectHistory History => _root.GetRequiredService<ProjectHistory>();

    private string HistoryFolder => Path.Combine(FolderPath, "history");

    [Fact]
    public async Task ReRecordingAVoice_EachStepReadsBackTheAudioItHad()
    {
        await SeedAsync();
        var voice = await CreateVoiceAsync("Alice", "Calm");
        var beforeAudio = await TipAsync();
        const string path = "voices/alice/Calm.wav";

        await WriteAsync(path, "first take");
        await CommitAsync(new SetVoiceAudioMutation(_folder, voice, path));
        var first = await TipAsync();
        await WriteAsync(path, "second take");
        await CommitAsync(new SetVoiceAudioMutation(_folder, voice, path));
        var second = await TipAsync();

        Assert.Equal("first take", await ReadAtAsync(first, path));
        Assert.Equal("second take", await ReadAtAsync(second, path));
        Assert.Null(await ReadAtAsync(beforeAudio, path));
    }

    [Fact]
    public async Task TheSameContent_LandsInBlobsOnce()
    {
        await SeedAsync();
        var calm = await CreateVoiceAsync("Alice", "Calm");
        var warm = await CreateVoiceAsync("Alice", "Warm");

        await WriteAsync("voices/alice/Calm.wav", "same take");
        await CommitAsync(new SetVoiceAudioMutation(_folder, calm, "voices/alice/Calm.wav"));
        await WriteAsync("voices/alice/Warm.wav", "same take");
        await CommitAsync(new SetVoiceAudioMutation(_folder, warm, "voices/alice/Warm.wav"));

        Assert.Single(Directory.GetFiles(Path.Combine(HistoryFolder, "blobs")));
        Assert.Equal("same take", await ReadAtAsync(await TipAsync(), "voices/alice/Calm.wav"));
    }

    [Fact]
    public async Task DeletingAVoice_RecordsItsAudioGone_AndEarlierStepsStillHaveIt()
    {
        await SeedAsync();
        var voice = await CreateVoiceAsync("Alice", "Calm");
        const string path = "voices/alice/Calm.wav";
        await WriteAsync(path, "take");
        await CommitAsync(new SetVoiceAudioMutation(_folder, voice, path));
        var recorded = await TipAsync();

        await _root.CreateAsyncScope().ServiceProvider.GetRequiredService<IVoiceAudioRemover>().DeleteVoiceAsync(_folder, voice);

        Assert.Null(await ReadAtAsync(await TipAsync(), path));
        Assert.Equal("take", await ReadAtAsync(recorded, path));
    }

    [Fact]
    public async Task EditingVoiceAudio_IsAStep_WithTheOriginal_AndRestoringItRecordsTheOriginalGone()
    {
        await SeedAsync();
        var voice = await CreateVoiceAsync("Alice", "Calm");
        var characterId = await CharacterIdAsync("Alice");
        const string path = "voices/alice/Calm.wav";
        await WriteAsync(path, "raw");
        await CommitAsync(new SetVoiceAudioMutation(_folder, voice, path));
        var raw = await TipAsync();
        var original = $"voices/{characterId}/{voice}.orig.wav";
        var editor = Editor();
        var voiceRef = new VoiceAudioRef(_folder, characterId, voice, path);

        await editor.ApplyAsync(voiceRef, "edited"u8.ToArray());
        var edited = await TipAsync();
        await editor.RestoreOriginalAsync(voiceRef);
        var restored = await TipAsync();

        Assert.NotEqual(raw, edited); // no row changes, but the audio does
        Assert.Equal(("edited", "raw"), (await ReadAtAsync(edited, path), await ReadAtAsync(edited, original)));
        Assert.Equal(("raw", null), (await ReadAtAsync(restored, path), await ReadAtAsync(restored, original)));
        Assert.Null(await ReadAtAsync(raw, original));
    }

    [Fact]
    public async Task ChangingTheCover_RecordsTheNewOne_AndTheOldOneGone()
    {
        await SeedAsync();
        await WriteAsync("cover.jpg", "first cover");
        await CommitAsync(new SetCoverImageMutation(_folder, "cover.jpg"));
        var first = await TipAsync();

        await WriteAsync("cover.png", "second cover");
        await CommitAsync(new SetCoverImageMutation(_folder, "cover.png"));
        File.Delete(Path.Combine(FolderPath, "cover.jpg")); // what the writer does once that commits
        var second = await TipAsync();

        Assert.Equal(("first cover", null), (await ReadAtAsync(first, "cover.jpg"), await ReadAtAsync(first, "cover.png")));
        Assert.Equal((null, "second cover"), (await ReadAtAsync(second, "cover.jpg"), await ReadAtAsync(second, "cover.png")));
    }

    [Fact]
    public async Task ACoverOverwrittenUnderItsOwnName_IsRecorded()
    {
        await SeedAsync();
        await WriteAsync("cover.jpg", "first cover");
        await CommitAsync(new SetCoverImageMutation(_folder, "cover.jpg"));
        var first = await TipAsync();

        await WriteAsync("cover.jpg", "second cover");
        await CommitAsync(new SetCoverImageMutation(_folder, "cover.jpg"));

        Assert.Equal("first cover", await ReadAtAsync(first, "cover.jpg"));
        Assert.Equal("second cover", await ReadAtAsync(await TipAsync(), "cover.jpg"));
    }

    [Fact]
    public async Task FilesThereWhenHistoryStarts_AreReadAtTheStartedStep()
    {
        await SeedAsync(sourceFile: "book.epub");
        await WriteAsync("book.epub", "the book");
        var voice = await SeedVoiceAsync("Alice", "Calm", "voices/alice/Calm.wav");
        await WriteAsync("voices/alice/Calm.wav", "take");

        await History.StartAsync(_folder);
        var started = await TipAsync();
        await WriteAsync("voices/alice/Calm.wav", "new take");
        await CommitAsync(new SetVoiceAudioMutation(_folder, voice, "voices/alice/Calm.wav"));

        Assert.Equal(("the book", "take"), (await ReadAtAsync(started, "book.epub"), await ReadAtAsync(started, "voices/alice/Calm.wav")));
        Assert.Equal(("the book", "new take"), (await ReadAtAsync(await TipAsync(), "book.epub"), await ReadAtAsync(await TipAsync(), "voices/alice/Calm.wav")));
    }

    [Fact]
    public async Task AFileWrittenOutsideAMutation_IsNotPinnedOnTheNextCommit()
    {
        await SeedAsync();
        var voice = await CreateVoiceAsync("Alice", "Calm");
        const string path = "voices/alice/Calm.wav";
        await WriteAsync(path, "take");
        await CommitAsync(new SetVoiceAudioMutation(_folder, voice, path));

        // A take staged for a commit that has not landed yet; another commit gets in first.
        await WriteAsync(path, "staged take");
        await CommitAsync(new CreateCharacterMutation(_folder, "Bob"));

        Assert.Equal("take", await ReadAtAsync(await TipAsync(), path));
    }

    [Fact]
    public async Task AFailedMutation_RecordsNoFile()
    {
        await SeedAsync();
        var voice = await CreateVoiceAsync("Alice", "Calm");
        const string path = "voices/alice/Calm.wav";
        await WriteAsync(path, "take");
        await CommitAsync(new SetVoiceAudioMutation(_folder, voice, path));
        var tip = await TipAsync();

        await WriteAsync(path, "rejected take");
        var outcome = await CommitAsync(new SetVoiceAudioMutation(_folder, Guid.NewGuid(), path));

        Assert.IsType<BookMutationOutcome.Rejected>(outcome);
        Assert.Equal(tip, await TipAsync());
        Assert.Equal("take", await ReadAtAsync(tip, path));
    }

    [Fact]
    public async Task AStepPastASnapshot_ReadsItsFilesFromIt()
    {
        await _root.DisposeAsync();
        _root = NewApp(snapshotEvery: 2);
        await SeedAsync();
        var voice = await CreateVoiceAsync("Alice", "Calm");
        await WriteAsync("voices/alice/Calm.wav", "take");
        await CommitAsync(new SetVoiceAudioMutation(_folder, voice, "voices/alice/Calm.wav"));
        await CommitAsync(new CreateCharacterMutation(_folder, "Bob"));
        var tip = await TipAsync();

        // The steps before the snapshot, and the base list, are not needed past it.
        foreach (var file in Directory.GetFiles(Path.Combine(HistoryFolder, "snapshots"), "base.*"))
            File.Delete(file);

        Assert.Equal("take", await ReadAtAsync(tip, "voices/alice/Calm.wav"));
    }

    [Fact]
    public async Task AnUnknownStep_IsUnknown()
    {
        await SeedAsync();
        await History.StartAsync(_folder);

        Assert.IsType<StepFile.UnknownStep>(await History.ReadFileAsync(_folder, Guid.NewGuid(), "cover.jpg"));
    }

    [Fact]
    public async Task AHistoryRecordedByAnOlderBuild_Restarts()
    {
        await SeedAsync();
        await CommitAsync(new CreateCharacterMutation(_folder, "Alice"));
        await using (var connection = new SqliteConnection($"Data Source={Path.Combine(HistoryFolder, "history.db")};Pooling=false"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version = 1";
            await command.ExecuteNonQueryAsync();
        }
        await _root.DisposeAsync();
        _root = NewApp(snapshotEvery: 50);

        var steps = (await History.ListAsync(_folder)).Steps;

        Assert.Equal("History started", Assert.Single(steps).Label);
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private ServiceProvider NewApp(int snapshotEvery)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddBookCommandHandlers();
        services.Configure<WorkspaceOptions>(o => o.FolderPath = TempDir);
        services.Configure<HistoryOptions>(o => o.SnapshotEvery = snapshotEvery);
        services.AddSingleton<IProjectDbContextFactory, ProjectDbContextProvider>();
        return services.BuildServiceProvider();
    }

    private VoiceAudioEditor Editor()
    {
        var scope = _root.CreateAsyncScope().ServiceProvider;
        return new VoiceAudioEditor(
            scope.GetRequiredService<IVoiceOriginalStore>(), scope.GetRequiredService<IFileSystem>(),
            scope.GetRequiredService<BookMutations>(), NullLogger<VoiceAudioEditor>.Instance);
    }

    private async Task SeedAsync(string? sourceFile = null)
    {
        await new BookHierarchyBuilder(OpenDbAsync).WithProject()
            .AddVolume("vol", v => v.AddChapter("ch", c => c
                .AddParagraph("para", p => p.AddNarration("item", "Hello world"))))
            .BuildAsync();
        if (sourceFile is null)
            return;
        await using var db = await OpenDbAsync();
        (await db.Projects.SingleAsync()).Filename = sourceFile;
        await db.SaveChangesAsync();
    }

    /// <summary>A Voice with audio already on disk, arranged before History starts.</summary>
    private async Task<Guid> SeedVoiceAsync(string character, string name, string audioFileName)
    {
        await using var db = await OpenDbAsync();
        var c = new Read2Me.Data.Entities.Character { Id = Guid.NewGuid(), Name = character };
        var voice = new Read2Me.Data.Entities.Voice { Id = Guid.NewGuid(), CharacterId = c.Id, Name = name, AudioFileName = audioFileName };
        db.Characters.Add(c);
        db.Voices.Add(voice);
        await db.SaveChangesAsync();
        return voice.Id;
    }

    private async Task<Guid> CreateVoiceAsync(string character, string name)
    {
        if (await CharacterIdOrNullAsync(character) is not { } characterId)
        {
            await CommitAsync(new CreateCharacterMutation(_folder, character));
            characterId = (await CharacterIdOrNullAsync(character))!.Value;
        }
        await CommitAsync(new CreateVoiceMutation(_folder, characterId, name));
        await using var db = await OpenDbAsync();
        return (await db.Voices.SingleAsync(v => v.CharacterId == characterId && v.Name == name)).Id;
    }

    private async Task<Guid> CharacterIdAsync(string name) => (await CharacterIdOrNullAsync(name))!.Value;

    private async Task<Guid?> CharacterIdOrNullAsync(string name)
    {
        await using var db = await OpenDbAsync();
        return (await db.Characters.SingleOrDefaultAsync(c => c.Name == name))?.Id;
    }

    private async Task WriteAsync(string relativePath, string content)
    {
        var path = Path.Combine(FolderPath, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, content);
    }

    private async Task<BookMutationOutcome> CommitAsync(BookMutation mutation)
    {
        await using var scope = _root.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<BookMutations>().CommitAsync(mutation);
    }

    private async Task<Guid> TipAsync() => (await History.ListAsync(_folder)).HeadStepId!.Value;

    /// <summary>The file's content as it stood after <paramref name="stepId"/>, or null when it didn't exist then.</summary>
    private async Task<string?> ReadAtAsync(Guid stepId, string relativePath) =>
        await History.ReadFileAsync(_folder, stepId, relativePath) switch
        {
            StepFile.Found found => await File.ReadAllTextAsync(found.Path),
            StepFile.Missing => null,
            var other => throw new Xunit.Sdk.XunitException($"Unexpected {other}"),
        };

    public override async ValueTask DisposeAsync()
    {
        await _root.DisposeAsync();
        await base.DisposeAsync();
    }
}
