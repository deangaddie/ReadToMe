using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Read2Me.Core.Configuration;
using Read2Me.Core.Models;
using Read2Me.Data;
using Read2Me.Data.Entities;
using Read2Me.Data.Enums;
using Read2Me.Services;
using Read2Me.Services.Events;
using Read2Me.Services.Mutations;
using Read2Me.Tests.Infrastructure;
using Xunit;

namespace Read2Me.Tests.Services.Mutations;

/// <summary>
/// The write-side spine, exercised through its one public entry point against the real
/// transaction-capable SQLite adapter — commit behaviour is the thing under test, so a fake store
/// would prove nothing. Manual ParagraphItem insertion is the first family to cross it end to end
/// (ADR 0007); the gated, exploding and half-staging mutations below exist only to reach the
/// commit, serialization and rollback rules a single well-behaved family cannot reach.
/// </summary>
public class BookMutationsTests : ProjectDbTestBase
{
    private readonly ServiceProvider _root;
    private readonly ProjectFolderId _folder;
    private readonly EventBroadcaster<BookMutationReceipt> _receipts;
    private readonly BookMutationOptions _options;

    public BookMutationsTests()
    {
        var services = new ServiceCollection();
        services.AddBookCommandHandlers();
        services.Configure<WorkspaceOptions>(o => o.FolderPath = TempDir);
        services.AddSingleton<IProjectDbContextFactory, ProjectDbContextProvider>();
        services.AddScoped<IBookMutationImplementation<GatedMutation>, GatedMutationImplementation>();
        services.AddScoped<IBookMutationImplementation<ExplodingMutation>, ExplodingMutationImplementation>();
        services.AddScoped<IBookMutationImplementation<HalfStagedMutation>, HalfStagedMutationImplementation>();
        _root = services.BuildServiceProvider();

        _folder = new ProjectFolderId(FolderName);
        _receipts = _root.GetRequiredService<EventBroadcaster<BookMutationReceipt>>();
        _options = _root.GetRequiredService<IOptions<BookMutationOptions>>().Value;
    }

    // ── committed insertion ──────────────────────────────────────────────────

    [Fact]
    public async Task Insert_Commits_AndReceiptDescribesWhatItActuallyChanged()
    {
        var b = await SeedOneItemAsync();
        await using var circuit = NewCircuit();

        var outcome = await circuit.Mutations.CommitAsync(
            new InsertParagraphItemMutation(_folder, b.ItemId("item"), InsertPosition.After, "A restored line."));

        var receipt = Assert.IsType<BookMutationOutcome.Committed>(outcome).Receipt;
        Assert.Equal(_folder, receipt.FolderId);
        Assert.Equal(nameof(InsertParagraphItemMutation), receipt.MutationName);
        Assert.NotEqual(Guid.Empty, receipt.MutationId);
        Assert.Equal(1L, receipt.Revision);
        Assert.NotNull(receipt.Effects.CreatedId);
        Assert.Equal(BookMutationScope.Exact, receipt.Effects.Scope);
        Assert.Equal(BookFacets.Structure, receipt.Effects.Facets);
        Assert.Equal(b.ParagraphId("para"), Assert.Single(receipt.Effects.ParagraphIds));
        Assert.Equal(receipt.Effects.CreatedId!.Value, Assert.Single(receipt.Effects.ParagraphItemIds));
        Assert.Empty(receipt.Effects.Structural);

        await using var verify = await OpenDbAsync();
        var inserted = await verify.ParagraphItems.FindAsync(receipt.Effects.CreatedId!.Value);
        Assert.Equal("A restored line.", inserted!.Text);
        Assert.Null(inserted.CharacterId);
    }

    [Fact]
    public async Task Insert_After_PersistsAnUnattributedItemInReadOrder()
    {
        // The anchor is fully attributed and voiced — precisely the case where inheriting would
        // look right and be wrong. The new item must arrive with none of it.
        var character = new Character { Id = Guid.NewGuid(), Name = "Alice" };
        var b = new BookHierarchyBuilder(OpenDbAsync);
        b.WithCharacter("alice", character);
        await b.AddVolume("vol", v => v.AddChapter("ch", c => c
            .AddParagraph("para", p => p
                .AddRawItem("anchor", ParagraphItemType.Speech, "“Hello there,” she said.", character.Id)
                .AddRawItem("tail", ParagraphItemType.Speech, "“Only me,” came the reply.", character.Id))))
            .BuildAsync();
        await using var circuit = NewCircuit();

        var receipt = await CommitInsertAsync(
            circuit, _folder, b.ItemId("anchor"), "  “And who might you be?” he answered.  ");

        await using var verify = await OpenDbAsync();
        var items = await verify.ParagraphItems
            .Where(i => i.ParagraphId == b.ParagraphId("para"))
            .OrderBy(i => i.Order)
            .ToListAsync();
        Assert.Equal([b.ItemId("anchor"), receipt.Effects.CreatedId!.Value, b.ItemId("tail")], items.Select(i => i.Id));

        var inserted = items[1];
        Assert.Equal(ParagraphItemType.Speech, inserted.ItemType);
        Assert.Equal("“And who might you be?” he answered.", inserted.Text);
        Assert.Null(inserted.CharacterId);
        Assert.Null(inserted.VoiceInstructions);
        Assert.Null(inserted.AudioFileName);

        // The anchor keeps everything it had — insertion is a sibling, not an edit.
        Assert.Equal(character.Id, items[0].CharacterId);
    }

    [Fact]
    public async Task Insert_Before_FirstItem_StaysInsideTheAnchorsParagraph()
    {
        var b = new BookHierarchyBuilder(OpenDbAsync);
        await b.AddVolume("vol", v => v.AddChapter("ch", c => c
            .AddParagraph("para1", p => p.AddNarration("first", "The door swung open."))
            .AddParagraph("para2", p => p.AddNarration("second", "Second paragraph."))))
            .BuildAsync();
        await using var circuit = NewCircuit();

        var outcome = await circuit.Mutations.CommitAsync(
            new InsertParagraphItemMutation(_folder, b.ItemId("second"), InsertPosition.Before, "A restored line."));

        var created = Assert.IsType<BookMutationOutcome.Committed>(outcome).Receipt.Effects.CreatedId!.Value;
        await using var verify = await OpenDbAsync();
        var items = await verify.ParagraphItems
            .Where(i => i.ParagraphId == b.ParagraphId("para2"))
            .OrderBy(i => i.Order)
            .ToListAsync();
        Assert.Equal([created, b.ItemId("second")], items.Select(i => i.Id));
        Assert.Single(await verify.ParagraphItems.Where(i => i.ParagraphId == b.ParagraphId("para1")).ToListAsync());
    }

    [Fact]
    public async Task Insert_Commits_AndTheSameSessionReadsTheNewItemBack()
    {
        // The tracking session caches one long-lived context per project; without eviction inside
        // the commit, the very circuit that wrote reads the Book from before its own write.
        var b = await SeedOneItemAsync();
        await using var circuit = NewCircuit();

        await circuit.Mutations.CommitAsync(
            new InsertParagraphItemMutation(_folder, b.ItemId("item"), InsertPosition.After, "Fresh."));

        var db = await circuit.Session.OpenAsync(_folder);
        Assert.Equal(2, await db.ParagraphItems.CountAsync(i => i.ParagraphId == b.ParagraphId("para")));
    }

    [Fact]
    public async Task Insert_Commits_PublishesTheReceiptOnlyAfterTheChangeIsReadable()
    {
        var b = await SeedOneItemAsync();
        await using var circuit = NewCircuit();
        var published = new List<BookMutationReceipt>();
        var visibleWhenPublished = new List<int>();
        void OnReceipt(BookMutationReceipt r)
        {
            published.Add(r);
            using var reader = OpenUnmigratedDb();
            visibleWhenPublished.Add(reader.ParagraphItems.Count(i => i.ParagraphId == b.ParagraphId("para")));
        }
        _receipts.Event += OnReceipt;

        try
        {
            await circuit.Mutations.CommitAsync(
                new InsertParagraphItemMutation(_folder, b.ItemId("item"), InsertPosition.After, "Published."));
        }
        finally { _receipts.Event -= OnReceipt; }

        Assert.Single(published);
        Assert.Equal(1L, published[0].Revision);
        Assert.Equal(2, Assert.Single(visibleWhenPublished));
    }

    [Fact]
    public async Task Insert_Commits_EvenWhenAReceiptSubscriberThrows()
    {
        var b = await SeedOneItemAsync();
        await using var circuit = NewCircuit();
        static void Explode(BookMutationReceipt _) =>
            throw new InvalidOperationException("A reader that cannot cope with its own mail.");
        _receipts.Event += Explode;

        BookMutationOutcome outcome;
        try
        {
            outcome = await circuit.Mutations.CommitAsync(
                new InsertParagraphItemMutation(_folder, b.ItemId("item"), InsertPosition.After, "Committed."));
        }
        finally { _receipts.Event -= Explode; }

        // Publication is best-effort and happens after the commit: a broken reader costs itself its
        // convergence, never the writer its write.
        Assert.IsType<BookMutationOutcome.Committed>(outcome);
        using var reader = OpenUnmigratedDb();
        Assert.Equal(2, reader.ParagraphItems.Count(i => i.ParagraphId == b.ParagraphId("para")));
    }

    [Fact]
    public async Task Revisions_AreMonotonicPerProject_AndIndependentAcrossProjects()
    {
        var b = await SeedOneItemAsync();
        var other = await SeedOneItemAsync("other-book");
        await using var circuit = NewCircuit();

        var first = await CommitInsertAsync(circuit, _folder, b.ItemId("item"), "One.");
        var second = await CommitInsertAsync(circuit, _folder, b.ItemId("item"), "Two.");
        var elsewhere = await CommitInsertAsync(circuit, "other-book", other.ItemId("item"), "Elsewhere.");

        Assert.Equal(1L, first.Revision);
        Assert.Equal(2L, second.Revision);
        Assert.Equal(1L, elsewhere.Revision);
    }

    // ── no change ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Insert_AgainstAPauseAnchor_IsNoChange_AndConsumesNoRevisionOrReceipt()
    {
        // A legal request that the Book refuses to act on: Speech inside a pause Paragraph is a
        // structure every reader assumes cannot exist.
        var b = new BookHierarchyBuilder(OpenDbAsync);
        await b.AddVolume("vol", v => v.AddChapter("ch", c => c
            .AddParagraph("para", p => p.AddPause("pause", ParagraphItemType.ParagraphPause))
            .AddParagraph("speech", p => p.AddNarration("item", "Real content."))))
            .BuildAsync();
        await using var circuit = NewCircuit();
        var published = new List<BookMutationReceipt>();
        void OnReceipt(BookMutationReceipt r) => published.Add(r);
        _receipts.Event += OnReceipt;

        BookMutationOutcome outcome;
        try
        {
            outcome = await circuit.Mutations.CommitAsync(
                new InsertParagraphItemMutation(_folder, b.ItemId("pause"), InsertPosition.After, "Text."));
        }
        finally { _receipts.Event -= OnReceipt; }

        Assert.IsType<BookMutationOutcome.NoChange>(outcome);
        Assert.Empty(published);
        await using var verify = await OpenDbAsync();
        Assert.Equal(1, await verify.ParagraphItems.CountAsync(i => i.ParagraphId == b.ParagraphId("para")));

        // No revision was consumed: the project's first *committed* mutation is still revision 1.
        var committed = await CommitInsertAsync(circuit, _folder, b.ItemId("item"), "First real write.");
        Assert.Equal(1L, committed.Revision);
    }

    // ── expected uncommitted outcomes ────────────────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Insert_WithWhitespaceOnlyText_IsRejectedAsValidation(string text)
    {
        var b = await SeedOneItemAsync();
        await using var circuit = NewCircuit();

        var outcome = await circuit.Mutations.CommitAsync(
            new InsertParagraphItemMutation(_folder, b.ItemId("item"), InsertPosition.After, text));

        var rejected = Assert.IsType<BookMutationOutcome.Rejected>(outcome);
        Assert.Equal(BookMutationRejection.Validation, rejected.Reason);
        await using var verify = await OpenDbAsync();
        Assert.Equal(1, await verify.ParagraphItems.CountAsync(i => i.ParagraphId == b.ParagraphId("para")));
    }

    [Fact]
    public async Task Insert_AgainstAnUnknownAnchor_IsRejectedAsNotFound()
    {
        await SeedOneItemAsync();
        await using var circuit = NewCircuit();

        var outcome = await circuit.Mutations.CommitAsync(
            new InsertParagraphItemMutation(_folder, Guid.NewGuid(), InsertPosition.After, "Nowhere."));

        Assert.Equal(BookMutationRejection.NotFound,
            Assert.IsType<BookMutationOutcome.Rejected>(outcome).Reason);
    }

    [Fact]
    public async Task Commit_WhenCancelledBeforeTheCommitPoint_IsRejectedAsCancelled_AndRollsBack()
    {
        var b = await SeedOneItemAsync();
        await using var circuit = NewCircuit();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var outcome = await circuit.Mutations.CommitAsync(
            new InsertParagraphItemMutation(_folder, b.ItemId("item"), InsertPosition.After, "Cancelled."), cts.Token);

        Assert.Equal(BookMutationRejection.Cancelled,
            Assert.IsType<BookMutationOutcome.Rejected>(outcome).Reason);
        await using var verify = await OpenDbAsync();
        Assert.Equal(1, await verify.ParagraphItems.CountAsync(i => i.ParagraphId == b.ParagraphId("para")));
    }

    [Fact]
    public async Task Commit_WhenAnImplementationRejectsAfterStagingWrites_RollsThoseWritesBack()
    {
        // The transaction belongs to BookMutations, so a half-applied implementation cannot leave a
        // half-committed Book behind — the failure mode the per-handler SaveChangesAsync calls had.
        var b = await SeedOneItemAsync();
        await using var circuit = NewCircuit();

        var outcome = await circuit.Mutations.CommitAsync(
            new HalfStagedMutation(_folder, b.ParagraphId("para")));

        Assert.Equal(BookMutationRejection.Conflict,
            Assert.IsType<BookMutationOutcome.Rejected>(outcome).Reason);
        await using var verify = await OpenDbAsync();
        Assert.Equal(1, await verify.ParagraphItems.CountAsync(i => i.ParagraphId == b.ParagraphId("para")));
    }

    [Fact]
    public async Task Commit_WhenAnImplementationHasADefect_Throws()
    {
        await SeedOneItemAsync();
        await using var circuit = NewCircuit();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => circuit.Mutations.CommitAsync(new ExplodingMutation(_folder)));
    }

    [Fact]
    public async Task Commit_OfAnUnregisteredMutation_Throws()
    {
        await using var circuit = NewCircuit();

        await Assert.ThrowsAsync<NotSupportedException>(
            () => circuit.Mutations.CommitAsync(new UnregisteredMutation(_folder)));
    }

    // ── serialization ────────────────────────────────────────────────────────

    [Fact]
    public async Task Writes_ForOneProject_SerializeInCommitOrder()
    {
        var b = await SeedOneItemAsync();
        await using var one = NewCircuit();
        await using var two = NewCircuit();

        var first = CommitInsertAsync(one, _folder, b.ItemId("item"), "First.");
        var second = CommitInsertAsync(two, _folder, b.ItemId("item"), "Second.");
        var receipts = await Task.WhenAll(first, second);

        Assert.Equal([1L, 2L], receipts.Select(r => r.Revision).Order());
        await using var verify = await OpenDbAsync();
        Assert.Equal(3, await verify.ParagraphItems.CountAsync(i => i.ParagraphId == b.ParagraphId("para")));
    }

    [Fact]
    public async Task Writes_ForDifferentProjects_DoNotWaitOnEachOther()
    {
        var other = await SeedOneItemAsync("other-book");
        await using var blocking = NewCircuit();
        await using var free = NewCircuit();
        var gate = new TaskCompletionSource();
        var entered = new TaskCompletionSource();

        var held = blocking.Mutations.CommitAsync(new GatedMutation(_folder, entered, gate));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // The other project's write must complete while this project's lock is still held.
        var elsewhere = await CommitInsertAsync(free, "other-book", other.ItemId("item"), "Unblocked.");

        Assert.Equal(1, elsewhere.Revision);
        gate.SetResult();
        await held;
    }

    [Fact]
    public async Task Commit_WhenTheLockWaitBudgetRunsOut_IsRejectedAsConflict()
    {
        var b = await SeedOneItemAsync();
        await using var blocking = NewCircuit();
        await using var waiting = NewCircuit();
        var gate = new TaskCompletionSource();
        var entered = new TaskCompletionSource();
        _options.LockWaitBudget = TimeSpan.FromMilliseconds(50);

        var held = blocking.Mutations.CommitAsync(new GatedMutation(_folder, entered, gate));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var outcome = await waiting.Mutations.CommitAsync(
            new InsertParagraphItemMutation(_folder, b.ItemId("item"), InsertPosition.After, "Blocked."));

        Assert.Equal(BookMutationRejection.Conflict,
            Assert.IsType<BookMutationOutcome.Rejected>(outcome).Reason);
        gate.SetResult();
        await held;
    }

    [Fact]
    public async Task Commit_HoldsTheWriteLockForItsTransactionOnly_NotAcrossPublication()
    {
        // A receipt subscriber stands in for the reconciliation the projection will do next: if the
        // lock were still held while receipts are published, this would time out rather than commit.
        var b = await SeedOneItemAsync();
        await using var circuit = NewCircuit();
        await using var subscriberCircuit = NewCircuit();
        _options.LockWaitBudget = TimeSpan.FromSeconds(2);
        BookMutationOutcome? fromSubscriber = null;
        var reentered = 0;
        void OnReceipt(BookMutationReceipt r)
        {
            if (Interlocked.Exchange(ref reentered, 1) == 1) return;
            fromSubscriber = Task.Run(() => subscriberCircuit.Mutations.CommitAsync(
                new InsertParagraphItemMutation(_folder, b.ItemId("item"), InsertPosition.After, "Reentrant.")))
                .GetAwaiter().GetResult();
        }
        _receipts.Event += OnReceipt;

        try
        {
            await circuit.Mutations.CommitAsync(
                new InsertParagraphItemMutation(_folder, b.ItemId("item"), InsertPosition.After, "First."));
        }
        finally { _receipts.Event -= OnReceipt; }

        Assert.IsType<BookMutationOutcome.Committed>(fromSubscriber);
    }

    [Fact]
    public async Task Commit_CancelledWhileWaitingForTheWriteLock_IsRejectedAsCancelled_NotConflict()
    {
        // A gesture the user abandoned and a project another writer is hogging are different
        // answers, and the caller reports them differently.
        var b = await SeedOneItemAsync();
        await using var blocking = NewCircuit();
        await using var waiting = NewCircuit();
        var gate = new TaskCompletionSource();
        var entered = new TaskCompletionSource();
        using var cts = new CancellationTokenSource();
        _options.LockWaitBudget = TimeSpan.FromSeconds(30);

        var held = blocking.Mutations.CommitAsync(new GatedMutation(_folder, entered, gate));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var pending = waiting.Mutations.CommitAsync(
            new InsertParagraphItemMutation(_folder, b.ItemId("item"), InsertPosition.After, "Abandoned."), cts.Token);
        await cts.CancelAsync();
        var outcome = await pending;

        Assert.Equal(BookMutationRejection.Cancelled,
            Assert.IsType<BookMutationOutcome.Rejected>(outcome).Reason);
        gate.SetResult();
        await held;
    }

    [Fact]
    public async Task Insert_ContendingWithAnActiveQueueWriter_StaysInsideTheStatedBudget()
    {
        // The budget a user gesture may spend queuing behind a background writer for the same
        // project: p95 of lock wait plus commit, for a single-item mutation, under 2 seconds.
        // Serialization is the cost this architecture accepts, and this is the number it accepts.
        // The queue writer here is a real committing writer, not a held lock, because what is being
        // measured is contention against work of the shape the Audio and Character queues do.
        var budget = TimeSpan.FromSeconds(2);
        const int samples = 20;
        var b = await SeedOneItemAsync();
        await using var queue = NewCircuit();
        await using var gesture = NewCircuit();
        using var queueRunning = new CancellationTokenSource();

        var queueWriter = Task.Run(async () =>
        {
            while (!queueRunning.IsCancellationRequested)
            {
                await queue.Mutations.CommitAsync(
                    new InsertParagraphItemMutation(_folder, b.ItemId("item"), InsertPosition.Before, "Queue write."));
                await Task.Delay(5, CancellationToken.None);
            }
        });

        var waits = new List<TimeSpan>(samples);
        for (var i = 0; i < samples; i++)
        {
            var started = Stopwatch.StartNew();
            var outcome = await gesture.Mutations.CommitAsync(
                new InsertParagraphItemMutation(_folder, b.ItemId("item"), InsertPosition.After, $"Gesture {i}."));
            started.Stop();
            Assert.IsType<BookMutationOutcome.Committed>(outcome);
            waits.Add(started.Elapsed);
        }

        await queueRunning.CancelAsync();
        await queueWriter;

        var p95 = waits.Order().ElementAt((int)Math.Ceiling(samples * 0.95) - 1);
        Assert.True(p95 < budget,
            $"p95 lock wait plus commit was {p95} over {samples} samples; budget {budget}. " +
            $"Slowest {waits.Max()}, median {waits.Order().ElementAt(samples / 2)}.");
    }

    // ── harness ──────────────────────────────────────────────────────────────

    /// <summary>One Blazor circuit's worth of scoped services — its own session, its own mutations.</summary>
    private sealed class Circuit(AsyncServiceScope scope) : IAsyncDisposable
    {
        public BookMutations Mutations { get; } = scope.ServiceProvider.GetRequiredService<BookMutations>();
        public ProjectDbSession Session { get; } = scope.ServiceProvider.GetRequiredService<ProjectDbSession>();
        public ValueTask DisposeAsync() => scope.DisposeAsync();
    }

    private Circuit NewCircuit() => new(_root.CreateAsyncScope());

    public override async ValueTask DisposeAsync()
    {
        await _root.DisposeAsync();
        await base.DisposeAsync();
    }

    private static async Task<BookMutationReceipt> CommitInsertAsync(
        Circuit circuit, ProjectFolderId folder, Guid anchorId, string text)
    {
        var outcome = await circuit.Mutations.CommitAsync(
            new InsertParagraphItemMutation(folder, anchorId, InsertPosition.After, text));
        return Assert.IsType<BookMutationOutcome.Committed>(outcome).Receipt;
    }

    private async Task<BookHierarchyBuilder> SeedOneItemAsync(string? folderName = null)
    {
        var b = new BookHierarchyBuilder(folderName == null ? OpenDbAsync : () => OpenDbAsync(folderName));
        await b.AddVolume("vol", v => v.AddChapter("ch", c => c
            .AddParagraph("para", p => p.AddNarration("item", "Hello world"))))
            .BuildAsync();
        return b;
    }

    private async Task<ProjectDbContext> OpenDbAsync(string folderName)
    {
        var path = Path.Combine(TempDir, folderName);
        Directory.CreateDirectory(path);
        var db = new ProjectDbContext(new DbContextOptionsBuilder<ProjectDbContext>()
            .UseSqlite($"Data Source={Path.Combine(path, "project.db")};Pooling=false")
            .Options);
        await db.Database.MigrateAsync();
        return db;
    }

    // ── test-only mutations ──────────────────────────────────────────────────

    /// <summary>Holds the project's write lock until released, so contention is observable.</summary>
    private sealed record GatedMutation(ProjectFolderId FolderId, TaskCompletionSource Entered, TaskCompletionSource Release)
        : BookMutation(FolderId);

    private sealed class GatedMutationImplementation : IBookMutationImplementation<GatedMutation>
    {
        public async Task<BookMutationEffects> ApplyAsync(GatedMutation mutation, ProjectDbContext db, CancellationToken ct)
        {
            mutation.Entered.TrySetResult();
            await mutation.Release.Task;
            return BookMutationEffects.Nothing;
        }
    }

    private sealed record ExplodingMutation(ProjectFolderId FolderId) : BookMutation(FolderId);

    private sealed class ExplodingMutationImplementation : IBookMutationImplementation<ExplodingMutation>
    {
        public Task<BookMutationEffects> ApplyAsync(ExplodingMutation mutation, ProjectDbContext db, CancellationToken ct) =>
            throw new InvalidOperationException("A defect, not an expected outcome.");
    }

    /// <summary>Stages a real write, then reports an expected failure — the rollback case.</summary>
    private sealed record HalfStagedMutation(ProjectFolderId FolderId, Guid ParagraphId) : BookMutation(FolderId);

    private sealed class HalfStagedMutationImplementation : IBookMutationImplementation<HalfStagedMutation>
    {
        public async Task<BookMutationEffects> ApplyAsync(HalfStagedMutation mutation, ProjectDbContext db, CancellationToken ct)
        {
            db.ParagraphItems.Add(new ParagraphItem
            {
                Id = Guid.NewGuid(),
                ParagraphId = mutation.ParagraphId,
                ItemType = ParagraphItemType.Speech,
                Text = "Staged then abandoned.",
                Order = "zz",
            });
            await db.SaveChangesAsync(ct);
            throw new BookMutationRejectedException(BookMutationRejection.Conflict, "Refused after staging.");
        }
    }

    private sealed record UnregisteredMutation(ProjectFolderId FolderId) : BookMutation(FolderId);
}
