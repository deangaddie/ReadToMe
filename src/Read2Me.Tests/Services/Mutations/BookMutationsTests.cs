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
        await using var caller = NewCaller();

        var outcome = await caller.Mutations.CommitAsync(
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
        Assert.Equal(b.ParagraphId("para"), receipt.Effects.CreatedParentId);
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
        await using var caller = NewCaller();

        var receipt = await CommitInsertAsync(
            caller, _folder, b.ItemId("anchor"), "  “And who might you be?” he answered.  ");

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
        await using var caller = NewCaller();

        var outcome = await caller.Mutations.CommitAsync(
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
        // the commit, the very caller that wrote reads the Book from before its own write.
        var b = await SeedOneItemAsync();
        await using var caller = NewCaller();

        await caller.Mutations.CommitAsync(
            new InsertParagraphItemMutation(_folder, b.ItemId("item"), InsertPosition.After, "Fresh."));

        var db = await caller.Session.OpenAsync(_folder);
        Assert.Equal(2, await db.ParagraphItems.CountAsync(i => i.ParagraphId == b.ParagraphId("para")));
    }

    [Fact]
    public async Task Insert_Commits_PublishesTheReceiptOnlyAfterTheChangeIsReadable()
    {
        var b = await SeedOneItemAsync();
        await using var caller = NewCaller();
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
            await caller.Mutations.CommitAsync(
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
        await using var caller = NewCaller();
        static void Explode(BookMutationReceipt _) =>
            throw new InvalidOperationException("A reader that cannot cope with its own mail.");
        _receipts.Event += Explode;

        BookMutationOutcome outcome;
        try
        {
            outcome = await caller.Mutations.CommitAsync(
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
        await using var caller = NewCaller();

        var first = await CommitInsertAsync(caller, _folder, b.ItemId("item"), "One.");
        var second = await CommitInsertAsync(caller, _folder, b.ItemId("item"), "Two.");
        var elsewhere = await CommitInsertAsync(caller, "other-book", other.ItemId("item"), "Elsewhere.");

        Assert.Equal(1L, first.Revision);
        Assert.Equal(2L, second.Revision);
        Assert.Equal(1L, elsewhere.Revision);
    }

    // ── expected uncommitted outcomes ────────────────────────────────────────
    // The refusal matrix lives in the BookHierarchy*Tests; here one refusal per mutation proves the
    // plan's rejection reaches the caller and commits nothing.

    [Fact]
    public async Task Insert_AgainstAPauseAnchor_IsRejectedAsValidation_AndConsumesNoRevisionOrReceipt()
    {
        // Speech inside a pause Paragraph is a structure every reader assumes cannot exist, so the
        // Book refuses it — the same answer InsertNode gives at a pause paragraph's start or end.
        var b = new BookHierarchyBuilder(OpenDbAsync);
        await b.AddVolume("vol", v => v.AddChapter("ch", c => c
            .AddParagraph("para", p => p.AddPause("pause", ParagraphItemType.ParagraphPause))
            .AddParagraph("speech", p => p.AddNarration("item", "Real content."))))
            .BuildAsync();
        await using var caller = NewCaller();
        var published = new List<BookMutationReceipt>();
        void OnReceipt(BookMutationReceipt r) => published.Add(r);
        _receipts.Event += OnReceipt;

        BookMutationOutcome outcome;
        try
        {
            outcome = await caller.Mutations.CommitAsync(
                new InsertParagraphItemMutation(_folder, b.ItemId("pause"), InsertPosition.After, "Text."));
        }
        finally { _receipts.Event -= OnReceipt; }

        var rejected = Assert.IsType<BookMutationOutcome.Rejected>(outcome);
        Assert.Equal(BookMutationRejection.Validation, rejected.Reason);
        Assert.Contains("A pause holds no speech", rejected.Message);
        Assert.Empty(published);
        await using var verify = await OpenDbAsync();
        Assert.Equal(1, await verify.ParagraphItems.CountAsync(i => i.ParagraphId == b.ParagraphId("para")));

        // No revision was consumed: the project's first *committed* mutation is still revision 1.
        var committed = await CommitInsertAsync(caller, _folder, b.ItemId("item"), "First real write.");
        Assert.Equal(1L, committed.Revision);
    }

    // ── node insertion ───────────────────────────────────────────────────────

    [Fact]
    public async Task InsertNode_ParagraphAfter_PersistsInReadingOrder_AndTheReceiptNamesIt()
    {
        var b = new BookHierarchyBuilder(OpenDbAsync);
        await b.AddVolume("vol", v => v.AddChapter("ch", c => c
            .AddParagraph("p1", p => p.AddNarration("i1", "First."))
            .AddParagraph("p2", p => p.AddNarration("i2", "Second."))))
            .BuildAsync();
        await using var caller = NewCaller();

        var outcome = await caller.Mutations.CommitAsync(new InsertNodeMutation(
            _folder, NodeKind.Paragraph, b.ParagraphId("p1"), Placement.After, null, "  A restored paragraph.  "));

        var receipt = Assert.IsType<BookMutationOutcome.Committed>(outcome).Receipt;
        var created = receipt.Effects.CreatedId!.Value;
        Assert.Equal(nameof(InsertNodeMutation), receipt.MutationName);
        Assert.Equal(BookMutationScope.Exact, receipt.Effects.Scope);
        Assert.Equal(BookFacets.Structure, receipt.Effects.Facets);
        Assert.Equal(created, Assert.Single(receipt.Effects.ParagraphIds));
        Assert.Equal(b.ChapterId("ch"), receipt.Effects.CreatedParentId);

        await using var verify = await OpenDbAsync();
        var paragraphs = await verify.Paragraphs
            .Where(p => p.ChapterId == b.ChapterId("ch"))
            .OrderBy(p => p.Order)
            .Select(p => p.Id)
            .ToListAsync();
        Assert.Equal([b.ParagraphId("p1"), created, b.ParagraphId("p2")], paragraphs);

        var item = Assert.Single(await verify.ParagraphItems.Where(i => i.ParagraphId == created).ToListAsync());
        Assert.Equal(Assert.Single(receipt.Effects.ParagraphItemIds), item.Id);
        Assert.Equal(ParagraphItemType.Speech, item.ItemType);
        Assert.Equal("A restored paragraph.", item.Text);
        Assert.Null(item.CharacterId);
        Assert.Null(item.VoiceInstructions);
        Assert.Null(item.AudioFileName);

        // Unattributed speech, so it raises the attribution-remaining count like any new dialog.
        await using var scope = _root.CreateAsyncScope();
        var counter = scope.ServiceProvider.GetRequiredService<IUnattributedItemCounter>();
        Assert.Equal(1, await counter.CountUnattributedCharacterItemsAsync(_folder, created));
    }

    [Fact]
    public async Task InsertNode_AtStartOfAnEmptyChapter_GivesItContent()
    {
        var b = new BookHierarchyBuilder(OpenDbAsync);
        await b.AddVolume("vol", v => v
            .AddChapter("full", c => c.AddParagraph("p1", p => p.AddNarration("i1", "First.")))
            .AddChapter("empty"))
            .BuildAsync();
        await using var caller = NewCaller();

        var outcome = await caller.Mutations.CommitAsync(new InsertNodeMutation(
            _folder, NodeKind.Chapter, b.ChapterId("empty"), Placement.AtStart, null, "At last, a line."));

        var created = Assert.IsType<BookMutationOutcome.Committed>(outcome).Receipt.Effects.CreatedId!.Value;
        await using var verify = await OpenDbAsync();
        var paragraph = await verify.Paragraphs.FindAsync(created);
        Assert.Equal(b.ChapterId("empty"), paragraph!.ChapterId);
        Assert.Single(await verify.Paragraphs.Where(p => p.ChapterId == b.ChapterId("full")).ToListAsync());
    }

    [Theory]
    [InlineData(Placement.AtStart)]
    [InlineData(Placement.AtEnd)]
    public async Task InsertNode_ItemAtAParagraphsEnd_PassesItsPause_AndTheReceiptNamesIt(Placement placement)
    {
        var b = new BookHierarchyBuilder(OpenDbAsync);
        await b.AddVolume("vol", v => v.AddChapter("ch", c => c
            .AddParagraph("para", p => p
                .AddPause("lead")
                .AddNarration("speech", "Between the pauses.")
                .AddPause("trail", ParagraphItemType.ParagraphPause))))
            .BuildAsync();
        await using var caller = NewCaller();

        var outcome = await caller.Mutations.CommitAsync(new InsertNodeMutation(
            _folder, NodeKind.Paragraph, b.ParagraphId("para"), placement, null, "  A new remark.  "));

        var receipt = Assert.IsType<BookMutationOutcome.Committed>(outcome).Receipt;
        var created = receipt.Effects.CreatedId!.Value;
        Assert.Equal(BookFacets.Structure, receipt.Effects.Facets);
        Assert.Equal(b.ParagraphId("para"), Assert.Single(receipt.Effects.ParagraphIds));
        Assert.Equal(created, Assert.Single(receipt.Effects.ParagraphItemIds));
        Assert.Equal(b.ParagraphId("para"), receipt.Effects.CreatedParentId);

        await using var verify = await OpenDbAsync();
        var items = await verify.ParagraphItems
            .Where(i => i.ParagraphId == b.ParagraphId("para"))
            .OrderBy(i => i.Order)
            .ToListAsync();
        var existing = new[] { b.ItemId("lead"), b.ItemId("speech"), b.ItemId("trail") };
        Assert.Equal(
            placement == Placement.AtStart ? [created, .. existing] : [.. existing, created],
            items.Select(i => i.Id));
        var item = items.Single(i => i.Id == created);
        Assert.Equal(ParagraphItemType.Speech, item.ItemType);
        Assert.Equal("A new remark.", item.Text);
        Assert.Null(item.CharacterId);
        Assert.Null(item.VoiceInstructions);
        Assert.Null(item.AudioFileName);

        await using var scope = _root.CreateAsyncScope();
        var counter = scope.ServiceProvider.GetRequiredService<IUnattributedItemCounter>();
        Assert.Equal(1, await counter.CountUnattributedCharacterItemsAsync(_folder, b.ParagraphId("para")));
    }

    [Fact]
    public async Task InsertNode_AgainstAnUnknownAnchor_IsRejectedAsNotFound()
    {
        await SeedOneItemAsync();
        await using var caller = NewCaller();

        var outcome = await caller.Mutations.CommitAsync(new InsertNodeMutation(
            _folder, NodeKind.Chapter, Guid.NewGuid(), Placement.AtEnd, null, "Nowhere."));

        var rejected = Assert.IsType<BookMutationOutcome.Rejected>(outcome);
        Assert.Equal(BookMutationRejection.NotFound, rejected.Reason);
        Assert.Contains("No chapter", rejected.Message);
    }

    [Fact]
    public async Task InsertNode_ChapterAfter_PersistsEmptyInReadingOrder_AndTheReceiptNamesIt()
    {
        var b = new BookHierarchyBuilder(OpenDbAsync);
        await b.AddVolume("vol", v => v
            .AddChapter("c1", c => c.AddParagraph("p1", p => p.AddNarration("i1", "First.")))
            .AddChapter("c2", c => c.AddParagraph("p2", p => p.AddNarration("i2", "Second."))))
            .BuildAsync();
        await using var caller = NewCaller();

        var outcome = await caller.Mutations.CommitAsync(new InsertNodeMutation(
            _folder, NodeKind.Chapter, b.ChapterId("c1"), Placement.After, "  Interlude ", null));

        var receipt = Assert.IsType<BookMutationOutcome.Committed>(outcome).Receipt;
        var created = receipt.Effects.CreatedId!.Value;
        Assert.Equal(BookFacets.Structure, receipt.Effects.Facets);
        Assert.Equal(created, Assert.Single(receipt.Effects.NodeIds));
        Assert.Empty(receipt.Effects.ParagraphIds);

        await using var verify = await OpenDbAsync();
        var chapter = await verify.Chapters.FindAsync(created);
        Assert.Equal("Interlude", chapter!.Title);
        Assert.Equal(chapter.PartId, receipt.Effects.CreatedParentId);
        var chapters = await verify.Chapters
            .Where(c => c.PartId == chapter.PartId)
            .OrderBy(c => c.Order)
            .Select(c => c.Id)
            .ToListAsync();
        Assert.Equal([b.ChapterId("c1"), created, b.ChapterId("c2")], chapters);
        Assert.False(await verify.Paragraphs.AnyAsync(p => p.ChapterId == created));
    }

    [Fact]
    public async Task InsertNode_VolumeBefore_WithABlankTitle_IsUntitledAndLandsFirst()
    {
        var b = await SeedOneItemAsync();
        await using var caller = NewCaller();

        var outcome = await caller.Mutations.CommitAsync(new InsertNodeMutation(
            _folder, NodeKind.Volume, b.VolumeId("vol"), Placement.Before, "   ", null));

        var effects = Assert.IsType<BookMutationOutcome.Committed>(outcome).Receipt.Effects;
        var created = effects.CreatedId!.Value;
        Assert.Null(effects.CreatedParentId);
        await using var verify = await OpenDbAsync();
        var volumes = await verify.Volumes.OrderBy(v => v.Order).ToListAsync();
        Assert.Equal([created, b.VolumeId("vol")], volumes.Select(v => v.Id));
        Assert.Equal("", volumes[0].Title);
        Assert.False(await verify.Parts.AnyAsync(p => p.VolumeId == created));
    }

    [Fact]
    public async Task InsertNode_AnEmptyPart_IsFilledByInsertingAtItsStart_AndSoIsItsChapter()
    {
        var b = await SeedOneItemAsync();
        await using var caller = NewCaller();

        async Task<Guid> Insert(NodeKind kind, Guid anchor, Placement placement, string? text = null) =>
            Assert.IsType<BookMutationOutcome.Committed>(await caller.Mutations.CommitAsync(
                new InsertNodeMutation(_folder, kind, anchor, placement, null, text))).Receipt.Effects.CreatedId!.Value;

        var part = await Insert(NodeKind.Volume, b.VolumeId("vol"), Placement.AtEnd);
        var chapter = await Insert(NodeKind.Part, part, Placement.AtStart);
        var paragraph = await Insert(NodeKind.Chapter, chapter, Placement.AtStart, "A first line.");

        await using var verify = await OpenDbAsync();
        Assert.Equal(b.VolumeId("vol"), (await verify.Parts.FindAsync(part))!.VolumeId);
        Assert.Equal(part, (await verify.Chapters.FindAsync(chapter))!.PartId);
        Assert.Equal(chapter, (await verify.Paragraphs.FindAsync(paragraph))!.ChapterId);
        Assert.Equal("A first line.", (await verify.ParagraphItems.SingleAsync(i => i.ParagraphId == paragraph)).Text);
    }

    [Fact]
    public async Task Commit_WhenCancelledBeforeTheCommitPoint_IsRejectedAsCancelled_AndRollsBack()
    {
        var b = await SeedOneItemAsync();
        await using var caller = NewCaller();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var outcome = await caller.Mutations.CommitAsync(
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
        await using var caller = NewCaller();

        var outcome = await caller.Mutations.CommitAsync(
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
        await using var caller = NewCaller();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => caller.Mutations.CommitAsync(new ExplodingMutation(_folder)));
    }

    [Fact]
    public async Task Commit_OfAnUnregisteredMutation_Throws()
    {
        await using var caller = NewCaller();

        await Assert.ThrowsAsync<NotSupportedException>(
            () => caller.Mutations.CommitAsync(new UnregisteredMutation(_folder)));
    }

    // ── serialization ────────────────────────────────────────────────────────

    [Fact]
    public async Task Writes_ForOneProject_SerializeInCommitOrder()
    {
        var b = await SeedOneItemAsync();
        await using var one = NewCaller();
        await using var two = NewCaller();

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
        await using var blocking = NewCaller();
        await using var free = NewCaller();
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
        await using var blocking = NewCaller();
        await using var waiting = NewCaller();
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
        await using var caller = NewCaller();
        await using var subscriberCaller = NewCaller();
        _options.LockWaitBudget = TimeSpan.FromSeconds(2);
        BookMutationOutcome? fromSubscriber = null;
        var reentered = 0;
        void OnReceipt(BookMutationReceipt r)
        {
            if (Interlocked.Exchange(ref reentered, 1) == 1) return;
            fromSubscriber = Task.Run(() => subscriberCaller.Mutations.CommitAsync(
                new InsertParagraphItemMutation(_folder, b.ItemId("item"), InsertPosition.After, "Reentrant.")))
                .GetAwaiter().GetResult();
        }
        _receipts.Event += OnReceipt;

        try
        {
            await caller.Mutations.CommitAsync(
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
        await using var blocking = NewCaller();
        await using var waiting = NewCaller();
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
        await using var queue = NewCaller();
        await using var gesture = NewCaller();
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

    // ── checkpoint staleness ─────────────────────────────────────────────────

    [Fact]
    public async Task ChapterSplit_stalesAnExistingStructureRow_inTheSameCommit_andReportsCheckpoints()
    {
        var b = await SeedTwoChaptersAsync();
        var confirmedAt = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        await SeedCheckpointAsync(PipelineStep.Structure, CheckpointState.Confirmed, at: confirmedAt);
        await SeedCheckpointAsync(PipelineStep.Speakers, CheckpointState.Confirmed);
        await using var caller = NewCaller();

        var outcome = await caller.Mutations.CommitAsync(new SplitAtChapterMutation(_folder, b.ChapterId("c2"), null));

        var receipt = Assert.IsType<BookMutationOutcome.Committed>(outcome).Receipt;
        Assert.Equal(BookFacets.Structure | BookFacets.Checkpoints, receipt.Effects.Facets);
        var rows = await CheckpointRowsAsync();
        var structure = rows.Single(r => r.Step == PipelineStep.Structure);
        Assert.True(structure.Stale);
        Assert.Equal(CheckpointState.Confirmed, structure.State);
        Assert.Equal(confirmedAt, structure.At);
        Assert.False(rows.Single(r => r.Step == PipelineStep.Speakers).Stale);
    }

    [Fact]
    public async Task ChapterSplit_withNoCheckpointRows_doesNotReportCheckpoints()
    {
        var b = await SeedTwoChaptersAsync();
        await using var caller = NewCaller();

        var outcome = await caller.Mutations.CommitAsync(new SplitAtChapterMutation(_folder, b.ChapterId("c2"), null));

        Assert.Equal(BookFacets.Structure,
            Assert.IsType<BookMutationOutcome.Committed>(outcome).Receipt.Effects.Facets);
        Assert.Empty(await CheckpointRowsAsync());
    }

    [Fact]
    public async Task ChapterSplit_withRowsAlreadyStale_doesNotReportCheckpoints()
    {
        var b = await SeedTwoChaptersAsync();
        await SeedCheckpointAsync(PipelineStep.Structure, CheckpointState.Confirmed, stale: true);
        await SeedCheckpointAsync(PipelineStep.Pauses, CheckpointState.Skipped, stale: true);
        await using var caller = NewCaller();

        var outcome = await caller.Mutations.CommitAsync(new SplitAtChapterMutation(_folder, b.ChapterId("c2"), null));

        Assert.Equal(BookFacets.Structure,
            Assert.IsType<BookMutationOutcome.Committed>(outcome).Receipt.Effects.Facets);
    }

    [Fact]
    public async Task FailedChapterSplit_leavesCheckpointRowsUntouched()
    {
        await SeedTwoChaptersAsync();
        await SeedCheckpointAsync(PipelineStep.Structure, CheckpointState.Confirmed);
        await using var caller = NewCaller();

        var outcome = await caller.Mutations.CommitAsync(new SplitAtChapterMutation(_folder, Guid.NewGuid(), null));

        Assert.IsType<BookMutationOutcome.Rejected>(outcome);
        Assert.False(Assert.Single(await CheckpointRowsAsync()).Stale);
    }

    [Fact]
    public async Task AddingChapterTitles_reportsStructure_butStalesNothing()
    {
        var b = new BookHierarchyBuilder(OpenDbAsync);
        await b.AddVolume("v1", v => v.AddChapter("Chapter One", c => c
                .AddParagraph("pg", g => g.AddNarration("i", "Text"))))
            .BuildAsync();
        await SeedCheckpointAsync(PipelineStep.Titles, CheckpointState.Skipped);
        await using var caller = NewCaller();

        var outcome = await caller.Mutations.CommitAsync(new AddChapterTitlesMutation(_folder));

        Assert.Equal(BookFacets.Structure,
            Assert.IsType<BookMutationOutcome.Committed>(outcome).Receipt.Effects.Facets);
        Assert.False(Assert.Single(await CheckpointRowsAsync()).Stale);
    }

    // ── harness ──────────────────────────────────────────────────────────────

    /// <summary>One caller's DI scope, as a request gets — its own session, its own mutations.</summary>
    private sealed class Caller(AsyncServiceScope scope) : IAsyncDisposable
    {
        public BookMutations Mutations { get; } = scope.ServiceProvider.GetRequiredService<BookMutations>();
        public ProjectDbSession Session { get; } = scope.ServiceProvider.GetRequiredService<ProjectDbSession>();
        public ValueTask DisposeAsync() => scope.DisposeAsync();
    }

    private Caller NewCaller() => new(_root.CreateAsyncScope());

    public override async ValueTask DisposeAsync()
    {
        await _root.DisposeAsync();
        await base.DisposeAsync();
    }

    private static async Task<BookMutationReceipt> CommitInsertAsync(
        Caller caller, ProjectFolderId folder, Guid anchorId, string text)
    {
        var outcome = await caller.Mutations.CommitAsync(
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

    private async Task<BookHierarchyBuilder> SeedTwoChaptersAsync()
    {
        var b = new BookHierarchyBuilder(OpenDbAsync);
        await b.AddVolume("v1", v => v.AddPart("p1", p => p
                .AddChapter("c1", c => c.AddParagraph("pg1", g => g.AddNarration("i1", "One")))
                .AddChapter("c2", c => c.AddParagraph("pg2", g => g.AddNarration("i2", "Two")))))
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
        : ProjectStateMutation(FolderId);

    private sealed class GatedMutationImplementation : IBookMutationImplementation<GatedMutation>
    {
        public async Task<BookMutationEffects> ApplyAsync(GatedMutation mutation, ProjectDbContext db, CancellationToken ct)
        {
            mutation.Entered.TrySetResult();
            await mutation.Release.Task;
            return BookMutationEffects.Nothing;
        }
    }

    private sealed record ExplodingMutation(ProjectFolderId FolderId) : ProjectStateMutation(FolderId);

    private sealed class ExplodingMutationImplementation : IBookMutationImplementation<ExplodingMutation>
    {
        public Task<BookMutationEffects> ApplyAsync(ExplodingMutation mutation, ProjectDbContext db, CancellationToken ct) =>
            throw new InvalidOperationException("A defect, not an expected outcome.");
    }

    /// <summary>Stages a real write, then reports an expected failure — the rollback case.</summary>
    private sealed record HalfStagedMutation(ProjectFolderId FolderId, Guid ParagraphId) : ProjectStateMutation(FolderId);

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

    private sealed record UnregisteredMutation(ProjectFolderId FolderId) : ProjectStateMutation(FolderId);
}
