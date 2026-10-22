using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Read2Me.Core.Configuration;
using Read2Me.Core.IO;
using Read2Me.Core.Models;
using Read2Me.Data;
using Read2Me.Data.Enums;
using Read2Me.Services;
using Read2Me.Services.Audio;
using Read2Me.Services.BookEdits;
using Read2Me.Services.Events;
using Read2Me.Services.History;
using Read2Me.Services.Mutations;
using Read2Me.Services.Mutations.Implementations;
using Read2Me.Services.Queueing;
using Read2Me.TestUtils;
using Read2Me.Tests.Infrastructure;
using Xunit;

namespace Read2Me.Tests.Services.History;

/// <summary>
/// <b>Restore</b> over a real migrated project: real mutations commit through
/// <see cref="BookMutations.CommitAsync"/>, a Restore brings an earlier step back, and the project
/// is asserted as it then stands — its rows, ids, audio, checkpoints and files — along with the
/// step History adds and what is refused meanwhile.
/// </summary>
public class RestoreTests : ProjectDbTestBase
{
    private readonly ServiceProvider _root;
    private readonly ProjectFolderId _folder;
    private readonly FakeBusy _busy = new();
    private readonly List<RestoreEvent> _events = [];
    private BookHierarchyBuilder _book = null!;
    private bool _throwBeforeCommit;

    public RestoreTests()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddBookCommandHandlers();
        services.Configure<WorkspaceOptions>(o => o.FolderPath = TempDir);
        services.AddSingleton<IProjectDbContextFactory, ProjectDbContextProvider>();
        services.AddSingleton<IProjectBusyAnswer>(_busy);
        services.AddScoped<IBookMutationImplementation<RestoreStepMutation>>(
            _ => new ThrowingRestore(new RestoreStepMutationImplementation(), this));
        _root = services.BuildServiceProvider();
        _folder = new ProjectFolderId(FolderName);
        _root.GetRequiredService<EventBroadcaster<RestoreEvent>>().Event += _events.Add;
    }

    private ProjectHistory History => _root.GetRequiredService<ProjectHistory>();

    // ── the project after a Restore ─────────────────────────────────────────

    [Fact]
    public async Task Restore_BringsBackTheStepsState_WithItsIds()
    {
        await SeedAsync();
        await CommitAsync(new CreateCharacterMutation(_folder, "Alice"));
        var alice = await CharacterIdAsync("Alice");
        var target = await TipAsync();

        await CommitAsync(new UpdateParagraphItemTextMutation(_folder, _book.ItemId("i1"), "Changed"));
        await CommitAsync(new DeleteParagraphMutation(_folder, _book.ParagraphId("p2")));
        await CommitAsync(new RenameCharacterMutation(_folder, alice, "Alicia"));
        await CommitAsync(new CreateCharacterMutation(_folder, "Bob"));

        Assert.IsType<RestoreOutcome.Restored>(await RestoreAsync(target));

        await using var db = await OpenDbAsync();
        var items = await db.ParagraphItems.OrderBy(i => i.Text).Select(i => new { i.Id, i.Text }).ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(
            [new { Id = _book.ItemId("i1"), Text = (string?)"One" }, new { Id = _book.ItemId("i3"), Text = (string?)"Three" }, new { Id = _book.ItemId("i2"), Text = (string?)"Two" }],
            items);
        Assert.Equal(_book.ParagraphId("p2"), (await db.Paragraphs.SingleAsync(p => p.Id == _book.ParagraphId("p2"), TestContext.Current.CancellationToken)).Id);
        var characters = await db.Characters.Where(c => c.Name != "Narrator").Select(c => new { c.Id, c.Name }).ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal([new { Id = alice, Name = "Alice" }], characters);
    }

    [Fact]
    public async Task Restore_IsANewStep_FromTheTarget_AndKeepsEveryLaterStep()
    {
        await SeedAsync();
        await CommitAsync(new CreateCharacterMutation(_folder, "Alice"));
        var target = (await History.ListAsync(_folder)).Steps[0];
        await CommitAsync(new CreateCharacterMutation(_folder, "Bob"));
        var before = await History.ListAsync(_folder);

        var restored = Assert.IsType<RestoreOutcome.Restored>(await RestoreAsync(target.StepId));

        var after = await History.ListAsync(_folder);
        var step = after.Steps[0];
        Assert.Equal(step, restored.Step);
        Assert.Equal(step.StepId, after.HeadStepId);
        Assert.Equal(target.StepId, step.RestoredFromStepId);
        Assert.Equal(HistoryStepKind.Restore, step.Kind);
        Assert.Null(step.Action);
        Assert.Equal(before.HeadStepId, step.ParentStepId);
        Assert.Matches(new Regex(@"^Restore to 'Create character' \(\d\d:\d\d\)$"), step.Label);
        Assert.False(step.IsOpen);
        Assert.Equal(before.Steps, after.Steps.Skip(1));
    }

    [Fact]
    public async Task TheRestoreStep_OpensAsTheTargetState()
    {
        await SeedAsync();
        await CommitAsync(new CreateCharacterMutation(_folder, "Alice"));
        var target = await TipAsync();
        await CommitAsync(new UpdateParagraphItemTextMutation(_folder, _book.ItemId("i1"), "Changed"));
        await CommitAsync(new DeleteParagraphMutation(_folder, _book.ParagraphId("p2")));

        var restored = Assert.IsType<RestoreOutcome.Restored>(await RestoreAsync(target));

        var opening = Assert.IsType<StepOpening.Opened>(await History.OpenStepAsync(_folder, restored.Step!.StepId, TestContext.Current.CancellationToken));
        await using var db = opening.State.OpenContext();
        Assert.Equal(["One", "Three", "Two"], await db.ParagraphItems.Select(i => i.Text!).OrderBy(t => t).ToListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Restore_DiscardsAudioOnlyOnItemsThatChange_AndReturningItemsComeBackWithout()
    {
        await SeedAsync();
        await CommitAsync(new CreateCharacterMutation(_folder, "Alice"));
        foreach (var item in new[] { "i1", "i2", "i3" })
            await RecordTakeAsync(item, $"{item}.wav");
        var target = await TipAsync();

        // i1's text and i2's speaker change, and each is generated again; i3 goes with its paragraph.
        await CommitAsync(new UpdateParagraphItemTextMutation(_folder, _book.ItemId("i1"), "Changed"));
        await RecordTakeAsync("i1", "i1-new.wav");
        await CommitAsync(new SetItemSpeakerMutation(_folder, _book.ItemId("i2"), await CharacterIdAsync("Alice")));
        await RecordTakeAsync("i2", "i2-new.wav");
        await CommitAsync(new DeleteParagraphMutation(_folder, _book.ParagraphId("p3")));
        await CommitAsync(new CreateCharacterMutation(_folder, "Bob")); // a later step that changes no item
        await CommitAsync(new InsertParagraphItemMutation(_folder, _book.ItemId("i2"), InsertPosition.After, "Four"));
        var four = await ItemIdAsync("Four");
        await RecordTakeAsync(four, "four.wav");

        var preview = await PreviewAsync(target);
        var restored = Assert.IsType<RestoreOutcome.Restored>(await RestoreAsync(target));

        Assert.Equal((3, 3), (preview, restored.AudioDiscardCount)); // i1, i2 and the item the target lacks
        Assert.Equal("Discarded audio on 3 items", restored.Step!.Summary);
        await using var db = await OpenDbAsync();
        var audio = await db.ParagraphItems.ToDictionaryAsync(i => i.Id, i => i.AudioFileName, TestContext.Current.CancellationToken);
        Assert.Equal(3, audio.Count);
        Assert.Null(audio[_book.ItemId("i1")]);
        Assert.Null(audio[_book.ItemId("i2")]);
        Assert.Null(audio[_book.ItemId("i3")]);
        Assert.Empty(await db.AudioReviews.ToListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Restore_CountsTheItemsThatNeedGenerating_TheDiscardedAndTheReturning_NotPauses()
    {
        await SeedAsync(pause: true); // a pause has no audio, and is never generated
        await CommitAsync(new InsertParagraphItemMutation(_folder, _book.ItemId("i3"), InsertPosition.After, "Four"));
        await CommitAsync(new InsertParagraphItemMutation(_folder, _book.ItemId("i3"), InsertPosition.After, "Five"));
        var four = await ItemIdAsync("Four");
        var five = await ItemIdAsync("Five");
        foreach (var item in new[] { _book.ItemId("i1"), _book.ItemId("i2"), _book.ItemId("i3"), four, five })
            await RecordTakeAsync(item, $"{item}.wav");
        var target = await TipAsync();

        // i1 and i2 change and are generated again; p3 goes with i3, Four and Five.
        await CommitAsync(new UpdateParagraphItemTextMutation(_folder, _book.ItemId("i1"), "Changed"));
        await RecordTakeAsync("i1", "i1-new.wav");
        await CommitAsync(new UpdateParagraphItemTextMutation(_folder, _book.ItemId("i2"), "Changed too"));
        await RecordTakeAsync("i2", "i2-new.wav");
        await CommitAsync(new DeleteParagraphMutation(_folder, _book.ParagraphId("p3")));

        var restored = Assert.IsType<RestoreOutcome.Restored>(await RestoreAsync(target));

        Assert.Equal((2, 5), (restored.AudioDiscardCount, restored.NeedsAudioCount));
    }

    [Fact]
    public async Task AnItemTheRestoreLeavesAsItIs_KeepsItsCurrentAudioAndReview()
    {
        await SeedAsync();
        await CommitAsync(new CreateCharacterMutation(_folder, "Alice"));
        var target = await TipAsync();
        await RecordTakeAsync("i1", "i1-later.wav"); // generated after the target: a result, not History
        await CommitAsync(new UpdateParagraphItemTextMutation(_folder, _book.ItemId("i2"), "Changed"));

        var restored = Assert.IsType<RestoreOutcome.Restored>(await RestoreAsync(target));

        Assert.Equal(0, restored.AudioDiscardCount);
        Assert.Null(restored.Step!.Summary);
        await using var db = await OpenDbAsync();
        Assert.Equal("i1-later.wav", (await db.ParagraphItems.SingleAsync(i => i.Id == _book.ItemId("i1"), TestContext.Current.CancellationToken)).AudioFileName);
        Assert.Equal(_book.ItemId("i1"), (await db.AudioReviews.SingleAsync(TestContext.Current.CancellationToken)).ParagraphItemId);
    }

    [Fact]
    public async Task Restore_StalesTheCheckpointsItDisturbs_AndOnlyThose()
    {
        await SeedAsync();
        await CommitAsync(new CreateCharacterMutation(_folder, "Alice"));
        var target = await TipAsync();
        await CommitAsync(new UpdateChapterTitleMutation(_folder, _book.ChapterId("ch"), "Renamed"));
        await SeedCheckpointAsync(PipelineStep.Structure, CheckpointState.Confirmed);
        await SeedCheckpointAsync(PipelineStep.Speakers, CheckpointState.Confirmed);
        await SeedCheckpointAsync(PipelineStep.Voices, CheckpointState.Confirmed);

        await RestoreAsync(target);

        var stale = (await CheckpointRowsAsync()).ToDictionary(c => c.Step, c => c.Stale);
        Assert.Equal(
            new Dictionary<PipelineStep, bool> { [PipelineStep.Structure] = true, [PipelineStep.Speakers] = false, [PipelineStep.Voices] = false },
            stale);
    }

    [Fact]
    public async Task Restore_BringsBackVoiceAudio_TheEditorsOriginal_TheCover_AndTheSourceFile()
    {
        await SeedAsync(sourceFile: "book.epub");
        await WriteAsync("book.epub", "the book");
        await CommitAsync(new CreateCharacterMutation(_folder, "Alice"));
        var characterId = await CharacterIdAsync("Alice");
        await CommitAsync(new CreateVoiceMutation(_folder, characterId, "Calm"));
        var voice = await VoiceIdAsync("Calm");
        const string path = "voices/alice/Calm.wav";
        await WriteAsync(path, "take");
        await CommitAsync(new SetVoiceAudioMutation(_folder, voice, path));
        await WriteAsync("cover.jpg", "first cover");
        await CommitAsync(new SetCoverImageMutation(_folder, "cover.jpg"));
        var target = await TipAsync();

        var original = $"voices/{characterId}/{voice}.orig.wav";
        await Editor().ApplyAsync(new VoiceAudioRef(_folder, characterId, voice, path), "edited"u8.ToArray());
        await WriteAsync("cover.png", "second cover");
        await CommitAsync(new SetCoverImageMutation(_folder, "cover.png"));
        await WriteAsync("book.epub", "another book");
        var edited = await TipAsync();

        await RestoreAsync(target);

        Assert.Equal("take", await ReadAsync(path));
        Assert.Null(await ReadAsync(original));
        Assert.Equal(("first cover", null), (await ReadAsync("cover.jpg"), await ReadAsync("cover.png")));
        Assert.Equal("the book", await ReadAsync("book.epub"));

        // And back again: the edit, with its original, returns from the blobs.
        await RestoreAsync(edited);

        Assert.Equal(("edited", "take"), (await ReadAsync(path), await ReadAsync(original)));
        Assert.Equal((null, "second cover"), (await ReadAsync("cover.jpg"), await ReadAsync("cover.png")));
    }

    [Fact]
    public async Task RestoringTheProjectAsItIs_ChangesNothing_AndAddsNoStep()
    {
        await SeedAsync();
        await CommitAsync(new CreateCharacterMutation(_folder, "Alice"));
        var before = await History.ListAsync(_folder);

        var restored = Assert.IsType<RestoreOutcome.Restored>(await RestoreAsync(before.HeadStepId!.Value));

        Assert.Null(restored.Step);
        Assert.Equal(before.Steps, (await History.ListAsync(_folder)).Steps);
    }

    // ── files after the commit, and the repair ──────────────────────────────

    [Fact]
    public async Task AFileWriteThatThrowsAfterTheCommit_LeavesTheRowsRestored_TheRestoreAnswered_AndTheRepairFixesIt()
    {
        var (path, target) = await SeedVoiceAndCoversAsync();
        // The voice's audio can't be written back: something stands where it goes.
        File.Delete(Path.Combine(FolderPath, path));
        Directory.CreateDirectory(Path.Combine(FolderPath, path));

        var restored = Assert.IsType<RestoreOutcome.Restored>(await RestoreAsync(target));

        Assert.Equal(restored.Step!.StepId, await TipAsync());
        Assert.Equal("cover.jpg", await CoverAsync());
        Assert.Equal(("first cover", null), (await ReadAsync("cover.jpg"), await ReadAsync("cover.png")));

        Directory.Delete(Path.Combine(FolderPath, path));
        await History.RepairAsync(_folder);

        Assert.Equal("take", await ReadAsync(path));
    }

    [Fact]
    public async Task AThrowBeforeTheCommit_ChangesNeitherRowsNorFiles()
    {
        var (path, target) = await SeedVoiceAndCoversAsync();
        var before = await History.ListAsync(_folder);
        _throwBeforeCommit = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() => RestoreAsync(target));

        Assert.Equal(before.Steps, (await History.ListAsync(_folder)).Steps);
        Assert.Equal("cover.png", await CoverAsync());
        Assert.Equal(("first cover", "second cover"), (await ReadAsync("cover.jpg"), await ReadAsync("cover.png")));
        Assert.Equal("edited", await ReadAsync(path));
    }

    [Fact]
    public async Task TheRepair_MakesTheStateFilesAsTheHeadListsThem_SweepsRestoreDebris_AndLeavesResultsAlone()
    {
        var (path, _) = await SeedVoiceAndCoversAsync();
        await WriteAsync(path, "changed");
        File.Delete(Path.Combine(FolderPath, "cover.png"));
        await WriteAsync("cover.jpg", "a cover the Head says is gone");
        await WriteAsync("audio/take.wav", "a take");
        await WriteAsync("history/cache/0123.restoring", "a crashed Restore's copy");
        await WriteAsync("history/cache/0123.aside/0", "a file an older build moved aside");

        await History.RepairAsync(_folder);

        Assert.Equal("edited", await ReadAsync(path));
        Assert.Equal(("second cover", null), (await ReadAsync("cover.png"), await ReadAsync("cover.jpg")));
        Assert.Equal("a take", await ReadAsync("audio/take.wav"));
        var cache = Path.Combine(FolderPath, "history", "cache");
        Assert.Empty(Directory.GetFiles(cache, "*.restoring"));
        Assert.Empty(Directory.GetDirectories(cache, "*.aside"));
    }

    // ── refusals ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Restore_IsRefusedWhileTheProjectIsBusy_AndChangesNothing()
    {
        await SeedAsync();
        await CommitAsync(new CreateCharacterMutation(_folder, "Alice"));
        var target = await TipAsync();
        await CommitAsync(new CreateCharacterMutation(_folder, "Bob"));
        var before = await History.ListAsync(_folder);
        _busy.Answer = new ProjectBusy([new BusySource(BusySourceKind.CharacterQueue, "Attribution: 3 paragraphs left")]);

        var refused = Assert.IsType<RestoreOutcome.Refused>(await RestoreAsync(target));

        Assert.Equal("Attribution: 3 paragraphs left", refused.Reason);
        Assert.Equal(before.Steps, (await History.ListAsync(_folder)).Steps);
        Assert.Empty(_events);
    }

    [Fact]
    public async Task UnknownStep_IsUnknown()
    {
        await SeedAsync();
        await History.StartAsync(_folder, TestContext.Current.CancellationToken);

        Assert.IsType<RestoreOutcome.UnknownStep>(await RestoreAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task DuringARestore_EveryOtherWrite_AndAnotherRestore_AreRefusedAtOnce()
    {
        await SeedAsync();
        await CommitAsync(new CreateCharacterMutation(_folder, "Alice"));
        var target = await TipAsync();
        await CommitAsync(new CreateCharacterMutation(_folder, "Bob"));
        BookMutationOutcome? write = null, results = null;
        RestoreOutcome? second = null;
        // Asked once the restoring flag is set, before the Restore commits.
        _busy.OnAsked = () =>
        {
            _busy.OnAsked = null;
            write = CommitAsync(new CreateCharacterMutation(_folder, "Carol")).GetAwaiter().GetResult();
            results = CommitAsync(new SetParagraphItemAudioMutation(_folder, _book.ItemId("i1"), "i1.wav")).GetAwaiter().GetResult();
            second = RestoreAsync(target).GetAwaiter().GetResult();
        };

        Assert.IsType<RestoreOutcome.Restored>(await RestoreAsync(target));

        Assert.Equal(new BookMutationOutcome.Rejected(BookMutationRejection.Restoring, "Project is restoring"), write);
        Assert.Equal(new BookMutationOutcome.Rejected(BookMutationRejection.Restoring, "Project is restoring"), results);
        Assert.Equal(new RestoreOutcome.Refused("Project is restoring"), second);
        Assert.False(_root.GetRequiredService<RestoringProjects>().IsRestoring(_folder));
        Assert.IsType<BookMutationOutcome.Committed>(await CommitAsync(new CreateCharacterMutation(_folder, "Carol")));
    }

    // ── what a Restore tells others ─────────────────────────────────────────

    [Fact]
    public async Task Restore_DropsTheProjectsEditWithAiSessions_AndAnnouncesItsStartAndFinish()
    {
        await SeedAsync();
        await CommitAsync(new CreateCharacterMutation(_folder, "Alice"));
        var target = await TipAsync();
        await CommitAsync(new CreateCharacterMutation(_folder, "Bob"));
        var store = _root.GetRequiredService<IBookEditSessionStore>();
        var mine = store.Create(_folder, Program(), [], "summary");
        var elsewhere = store.Create(new ProjectFolderId("another-book"), Program(), [], "summary");

        var restored = Assert.IsType<RestoreOutcome.Restored>(await RestoreAsync(target));

        Assert.Null(store.TryGet(_folder, mine.Id));
        Assert.NotNull(store.TryGet(new ProjectFolderId("another-book"), elsewhere.Id));
        Assert.Equal(
            [(RestoreEventKind.Started, (Guid?)null, ""), (RestoreEventKind.Finished, restored.Step!.StepId, mine.Id)],
            _events.Select(e => (e.Kind, e.Step?.StepId, string.Join(",", e.DroppedEditSessions))));
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    /// <summary>
    /// A Voice whose audio was edited and a cover changed after the target step: the voice's audio
    /// is "take" at the target and "edited" now, the cover cover.jpg then and cover.png now.
    /// </summary>
    private async Task<(string VoicePath, Guid Target)> SeedVoiceAndCoversAsync()
    {
        await SeedAsync();
        await CommitAsync(new CreateCharacterMutation(_folder, "Alice"));
        var characterId = await CharacterIdAsync("Alice");
        await CommitAsync(new CreateVoiceMutation(_folder, characterId, "Calm"));
        var voice = await VoiceIdAsync("Calm");
        const string path = "voices/alice/Calm.wav";
        await WriteAsync(path, "take");
        await CommitAsync(new SetVoiceAudioMutation(_folder, voice, path));
        await WriteAsync("cover.jpg", "first cover");
        await CommitAsync(new SetCoverImageMutation(_folder, "cover.jpg"));
        var target = await TipAsync();

        await Editor().ApplyAsync(new VoiceAudioRef(_folder, characterId, voice, path), "edited"u8.ToArray());
        await WriteAsync("cover.png", "second cover");
        await CommitAsync(new SetCoverImageMutation(_folder, "cover.png"));
        return (path, target);
    }

    private async Task<string?> CoverAsync()
    {
        await using var db = await OpenDbAsync();
        return (await db.Projects.SingleAsync()).CoverImage;
    }

    /// <summary>The real Restore, which throws once it has applied, before the commit, when the test says so.</summary>
    private sealed class ThrowingRestore(RestoreStepMutationImplementation real, RestoreTests test)
        : IBookMutationImplementation<RestoreStepMutation>
    {
        public async Task<BookMutationEffects> ApplyAsync(RestoreStepMutation mutation, ProjectDbContext db, CancellationToken ct)
        {
            var effects = await real.ApplyAsync(mutation, db, ct);
            return test._throwBeforeCommit ? throw new InvalidOperationException("Injected before the commit.") : effects;
        }
    }

    private sealed class FakeBusy : IProjectBusyAnswer
    {
        public ProjectBusy? Answer { get; set; }
        public Action? OnAsked { get; set; }

        public ProjectBusy? For(ProjectFolderId folder)
        {
            OnAsked?.Invoke();
            return Answer;
        }
    }

    private static EditProgram Program() => new(true, null, EditTargetSelector.ParagraphText, NodeFilter.All, ParagraphFilter.All,
        new EditTransform(TransformKind.ChangeCase, CaseMode: CaseMode.Upper));

    private async Task SeedAsync(string? sourceFile = null, bool pause = false)
    {
        _book = new BookHierarchyBuilder(OpenDbAsync).WithProject()
            .AddVolume("vol", v => v.AddChapter("ch", c =>
            {
                c.AddParagraph("p1", p => p.AddNarration("i1", "One"))
                    .AddParagraph("p2", p => p.AddNarration("i2", "Two"))
                    .AddParagraph("p3", p => p.AddNarration("i3", "Three"));
                if (pause)
                    c.AddParagraph("p4", p => p.AddPause("pause", ParagraphItemType.ChapterPause));
            }));
        await _book.BuildAsync();
        if (sourceFile is not null)
        {
            await using var db = await OpenDbAsync();
            (await db.Projects.SingleAsync()).Filename = sourceFile;
            await db.SaveChangesAsync();
        }
    }

    private async Task<RestoreOutcome> RestoreAsync(Guid stepId)
    {
        await using var scope = _root.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<HistoryRestore>().RestoreAsync(_folder, stepId);
    }

    private async Task<int> PreviewAsync(Guid stepId)
    {
        var opened = Assert.IsType<StepOpening.Opened>(await History.OpenStepAsync(_folder, stepId));
        return await History.CountRestoreAudioDiscardsAsync(_folder, opened.State);
    }

    private async Task<BookMutationOutcome> CommitAsync(BookMutation mutation)
    {
        await using var scope = _root.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<BookMutations>().CommitAsync(mutation);
    }

    /// <summary>A generated take with a verdict that asks for review: what the Audio Queue records.</summary>
    private Task RecordTakeAsync(string item, string path) => RecordTakeAsync(_book.ItemId(item), path);

    private async Task RecordTakeAsync(Guid itemId, string path) =>
        Assert.IsType<BookMutationOutcome.Committed>(await CommitAsync(new RecordParagraphItemAudioMutation(_folder, itemId, path,
            new AudioReviewVerdict(true, null, false, 0.5, "too many errors", "heard", "said"))));

    private async Task<Guid> TipAsync() => (await History.ListAsync(_folder)).HeadStepId!.Value;

    private async Task<Guid> CharacterIdAsync(string name)
    {
        await using var db = await OpenDbAsync();
        return (await db.Characters.SingleAsync(c => c.Name == name)).Id;
    }

    private async Task<Guid> VoiceIdAsync(string name)
    {
        await using var db = await OpenDbAsync();
        return (await db.Voices.SingleAsync(v => v.Name == name)).Id;
    }

    private async Task<Guid> ItemIdAsync(string text)
    {
        await using var db = await OpenDbAsync();
        return (await db.ParagraphItems.SingleAsync(i => i.Text == text)).Id;
    }

    private VoiceAudioEditor Editor()
    {
        var scope = _root.CreateAsyncScope().ServiceProvider;
        return new VoiceAudioEditor(
            scope.GetRequiredService<IVoiceOriginalStore>(), scope.GetRequiredService<IFileSystem>(),
            scope.GetRequiredService<BookMutations>(), NullLogger<VoiceAudioEditor>.Instance);
    }

    private async Task WriteAsync(string relativePath, string content)
    {
        var path = Path.Combine(FolderPath, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, content);
    }

    private async Task<string?> ReadAsync(string relativePath)
    {
        var path = Path.Combine(FolderPath, relativePath);
        return File.Exists(path) ? await File.ReadAllTextAsync(path) : null;
    }

    public override async ValueTask DisposeAsync()
    {
        await _root.DisposeAsync();
        await base.DisposeAsync();
    }
}
