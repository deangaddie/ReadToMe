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

    public RestoreTests()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddBookCommandHandlers();
        services.Configure<WorkspaceOptions>(o => o.FolderPath = TempDir);
        services.AddSingleton<IProjectDbContextFactory, ProjectDbContextProvider>();
        services.AddSingleton<IProjectBusyAnswer>(_busy);
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

    private async Task SeedAsync(string? sourceFile = null)
    {
        _book = new BookHierarchyBuilder(OpenDbAsync).WithProject()
            .AddVolume("vol", v => v.AddChapter("ch", c => c
                .AddParagraph("p1", p => p.AddNarration("i1", "One"))
                .AddParagraph("p2", p => p.AddNarration("i2", "Two"))
                .AddParagraph("p3", p => p.AddNarration("i3", "Three"))));
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
