using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Read2Me.Core.Configuration;
using Read2Me.Core.Models;
using Read2Me.Data;
using Read2Me.Data.Entities;
using Read2Me.Data.Enums;
using Read2Me.Services;
using Read2Me.Services.Commands;
using Read2Me.Services.Commands.Handlers;
using Read2Me.Services.IO;
using Read2Me.Services.Mutations;
using Read2Me.Tests.Infrastructure;
using Xunit;

namespace Read2Me.Tests.Services.Characters
{
    /// <summary>
    /// Release narration, through the command the generic endpoint posts: what it clears, what it
    /// leaves alone at each scope, and what the receipt names. Follows
    /// <see cref="SetParagraphsCharacterHandlerTests"/>.
    /// </summary>
    public class ReleaseNarrationHandlerTests : ProjectDbTestBase
    {
        private readonly ServiceProvider _root;
        private readonly ReleaseNarrationHandler _handler;
        private readonly ProjectReader _reader;
        private readonly ProjectFolderId _folder;

        private static readonly Guid AliceId = Guid.NewGuid();

        public ReleaseNarrationHandlerTests()
        {
            var services = new ServiceCollection();
            services.AddBookCommandHandlers();
            services.Configure<WorkspaceOptions>(o => o.FolderPath = TempDir);
            services.AddSingleton<IProjectDbContextFactory, ProjectDbContextProvider>();
            _root = services.BuildServiceProvider();

            _handler = new ReleaseNarrationHandler(_root.GetRequiredService<BookMutations>());
            _folder = new ProjectFolderId(FolderName);

            var fs = new FileSystemService(Options.Create(new WorkspaceOptions { FolderPath = TempDir }));
            var session = new ProjectDbSession(fs, new ProjectDbContextProvider(), NullLogger<ProjectDbSession>.Instance);
            _reader = new ProjectReader(session, NullLogger<ProjectReader>.Instance);
        }

        public override async ValueTask DisposeAsync()
        {
            await _root.DisposeAsync();
            await base.DisposeAsync();
        }

        /// <summary>
        /// Part 1 / Chapter 1: a transcript (q, a), a quoted paragraph with a tag, a paragraph of
        /// unattributed dialog with a tag, attributed dialog, a lone pause, and narration followed
        /// by a pause. Part 1 / Chapter 2 and Part 2 / Chapter 3 each hold one more narration-only
        /// paragraph, so every scope has something outside it.
        /// </summary>
        private async Task<BookHierarchyBuilder> SeedAsync()
        {
            var b = new BookHierarchyBuilder(OpenDbAsync);
            b.WithCharacter("alice", new Character { Id = AliceId, Name = "Alice" });
            await b.AddVolume("vol", v => v
                .AddPart("part1", p => p
                    .AddChapter("ch1", c => c
                        .AddParagraph("q", p => p.AddNarration("q-item", "Q: Who are you?"))
                        .AddParagraph("a", p => p.AddNarration("a-item", "A: Hari Seldon."))
                        .AddParagraph("tagged", p => p
                            .AddRawItem("tagged-dialog", ParagraphItemType.Speech, "\"Hi,\"", AliceId)
                            .AddNarration("tagged-tag", "he said."))
                        .AddParagraph("open", p => p
                            .AddRawItem("open-dialog", ParagraphItemType.Speech, "\"Who?\"", null)
                            .AddNarration("open-tag", "she asked."))
                        .AddParagraph("attributed", p => p
                            .AddRawItem("attributed-dialog", ParagraphItemType.Speech, "\"Me.\"", AliceId))
                        .AddParagraph("pause", p => p.AddPause("pause-item"))
                        .AddParagraph("trailing", p => p
                            .AddNarration("trailing-narration", "Silence fell.")
                            .AddPause("trailing-pause")))
                    .AddChapter("ch2", c => c
                        .AddParagraph("ch2-p", p => p.AddNarration("ch2-item", "Elsewhere."))))
                .AddPart("part2", p => p
                    .AddChapter("ch3", c => c
                        .AddParagraph("ch3-p", p => p.AddNarration("ch3-item", "Later.")))))
                .BuildAsync();
            return b;
        }

        private Task<BookCommandResult> ReleaseAsync(NarrationReleaseScope scope, Guid nodeId) =>
            _handler.HandleAsync(new ReleaseNarrationCommand(_folder, scope, nodeId), CancellationToken.None);

        private async Task<ParagraphItem> ItemAsync(BookHierarchyBuilder b, string name)
        {
            await using var db = await OpenDbAsync();
            return (await db.ParagraphItems.FindAsync(b.ItemId(name)))!;
        }

        private async Task SetAudioAsync(BookHierarchyBuilder b, params string[] names)
        {
            await using var db = await OpenDbAsync();
            foreach (var name in names)
                (await db.ParagraphItems.FindAsync(b.ItemId(name)))!.AudioFileName = $"audio/{name}.wav";
            await db.SaveChangesAsync();
        }

        [Fact]
        public async Task Release_ClearsTheNarratorStampFromNarrationOnlyParagraphs()
        {
            var b = await SeedAsync();

            await ReleaseAsync(NarrationReleaseScope.Chapter, b.ChapterId("ch1"));

            Assert.Null((await ItemAsync(b, "q-item")).CharacterId);
            Assert.Null((await ItemAsync(b, "a-item")).CharacterId);
            Assert.Null((await ItemAsync(b, "trailing-narration")).CharacterId);
        }

        [Fact]
        public async Task Release_LeavesSpeechTagsInQuotedParagraphsAsNarration()
        {
            var b = await SeedAsync();

            await ReleaseAsync(NarrationReleaseScope.Chapter, b.ChapterId("ch1"));

            Assert.Equal(ProjectDbContext.NarratorId, (await ItemAsync(b, "tagged-tag")).CharacterId);
            Assert.Equal(ProjectDbContext.NarratorId, (await ItemAsync(b, "open-tag")).CharacterId);
        }

        [Fact]
        public async Task Release_LeavesDialogAndPausesUntouched()
        {
            var b = await SeedAsync();

            await ReleaseAsync(NarrationReleaseScope.Chapter, b.ChapterId("ch1"));

            Assert.Equal(AliceId, (await ItemAsync(b, "tagged-dialog")).CharacterId);
            Assert.Equal(AliceId, (await ItemAsync(b, "attributed-dialog")).CharacterId);
            Assert.Null((await ItemAsync(b, "open-dialog")).CharacterId);
            var pause = await ItemAsync(b, "pause-item");
            Assert.Equal(ParagraphItemType.Pause, pause.ItemType);
            Assert.Null(pause.CharacterId);
            Assert.Equal(ParagraphItemType.Pause, (await ItemAsync(b, "trailing-pause")).ItemType);
        }

        [Fact]
        public async Task Release_KeepsTextBoundariesAndVoiceInstructions()
        {
            var b = await SeedAsync();
            await using (var db = await OpenDbAsync())
            {
                (await db.ParagraphItems.FindAsync(b.ItemId("q-item")))!.VoiceInstructions = "stern";
                await db.SaveChangesAsync();
            }

            await ReleaseAsync(NarrationReleaseScope.Chapter, b.ChapterId("ch1"));

            var q = await ItemAsync(b, "q-item");
            Assert.Equal("Q: Who are you?", q.Text);
            Assert.Equal("stern", q.VoiceInstructions);
            Assert.Equal(ParagraphItemType.Speech, q.ItemType);
            await using var verify = await OpenDbAsync();
            Assert.Equal(2, verify.ParagraphItems.Count(i => i.ParagraphId == b.ParagraphId("trailing")));
        }

        [Fact]
        public async Task Release_DropsAudioOnlyFromReleasedItems()
        {
            var b = await SeedAsync();
            await SetAudioAsync(b, "q-item", "tagged-tag", "tagged-dialog", "ch2-item");

            var result = await ReleaseAsync(NarrationReleaseScope.Chapter, b.ChapterId("ch1"));

            Assert.Null((await ItemAsync(b, "q-item")).AudioFileName);
            Assert.NotNull((await ItemAsync(b, "tagged-tag")).AudioFileName);
            Assert.NotNull((await ItemAsync(b, "tagged-dialog")).AudioFileName);
            Assert.NotNull((await ItemAsync(b, "ch2-item")).AudioFileName);
            var receipt = Assert.IsType<BookMutationOutcome.Committed>(result.Outcome).Receipt;
            Assert.Equal(BookFacets.Attribution | BookFacets.Audio, receipt.Effects.Facets);
        }

        [Fact]
        public async Task Release_ReceiptNamesTheReleasedParagraphsAndItems()
        {
            var b = await SeedAsync();

            var result = await ReleaseAsync(NarrationReleaseScope.Chapter, b.ChapterId("ch1"));

            var receipt = Assert.IsType<BookMutationOutcome.Committed>(result.Outcome).Receipt;
            Assert.Equal(BookMutationScope.Exact, receipt.Effects.Scope);
            // No released item had audio, so the receipt does not claim any was invalidated.
            Assert.Equal(BookFacets.Attribution, receipt.Effects.Facets);
            Assert.Equal(
                new HashSet<Guid> { b.ParagraphId("q"), b.ParagraphId("a"), b.ParagraphId("trailing") },
                receipt.Effects.ParagraphIds.ToHashSet());
            Assert.Equal(
                new HashSet<Guid> { b.ItemId("q-item"), b.ItemId("a-item"), b.ItemId("trailing-narration") },
                receipt.Effects.ParagraphItemIds.ToHashSet());
            Assert.Null(result.EntityId);
        }

        [Fact]
        public async Task Release_ChapterScope_LeavesOtherChaptersAlone()
        {
            var b = await SeedAsync();

            await ReleaseAsync(NarrationReleaseScope.Chapter, b.ChapterId("ch1"));

            Assert.Equal(ProjectDbContext.NarratorId, (await ItemAsync(b, "ch2-item")).CharacterId);
            Assert.Equal(ProjectDbContext.NarratorId, (await ItemAsync(b, "ch3-item")).CharacterId);
        }

        [Fact]
        public async Task Release_PartScope_ReachesEveryChapterOfThePartAndNoOther()
        {
            var b = await SeedAsync();

            await ReleaseAsync(NarrationReleaseScope.Part, b.PartId("part1"));

            Assert.Null((await ItemAsync(b, "q-item")).CharacterId);
            Assert.Null((await ItemAsync(b, "ch2-item")).CharacterId);
            Assert.Equal(ProjectDbContext.NarratorId, (await ItemAsync(b, "tagged-tag")).CharacterId);
            Assert.Equal(ProjectDbContext.NarratorId, (await ItemAsync(b, "ch3-item")).CharacterId);
        }

        [Fact]
        public async Task Release_ParagraphScope_ReachesOnlyThatParagraph()
        {
            var b = await SeedAsync();

            await ReleaseAsync(NarrationReleaseScope.Paragraph, b.ParagraphId("q"));

            Assert.Null((await ItemAsync(b, "q-item")).CharacterId);
            Assert.Equal(ProjectDbContext.NarratorId, (await ItemAsync(b, "a-item")).CharacterId);
            Assert.Equal(ProjectDbContext.NarratorId, (await ItemAsync(b, "ch2-item")).CharacterId);
        }

        [Fact]
        public async Task Release_ParagraphScope_OnAQuotedParagraph_IsNoChange()
        {
            var b = await SeedAsync();

            var result = await ReleaseAsync(NarrationReleaseScope.Paragraph, b.ParagraphId("tagged"));

            Assert.IsType<BookMutationOutcome.NoChange>(result.Outcome);
            Assert.Equal(ProjectDbContext.NarratorId, (await ItemAsync(b, "tagged-tag")).CharacterId);
        }

        [Fact]
        public async Task Release_NothingToRelease_IsNoChange_AndConsumesNoRevision()
        {
            var b = await SeedAsync();
            var revisions = _root.GetRequiredService<BookRevisionSequence>();
            await ReleaseAsync(NarrationReleaseScope.Part, b.PartId("part1"));
            var before = revisions.Current(_folder);

            // A second release finds nothing left to clear.
            var result = await ReleaseAsync(NarrationReleaseScope.Part, b.PartId("part1"));

            Assert.IsType<BookMutationOutcome.NoChange>(result.Outcome);
            Assert.Equal(before, revisions.Current(_folder));
        }

        [Fact]
        public async Task Release_UnknownNode_IsNoChange()
        {
            await SeedAsync();

            var result = await ReleaseAsync(NarrationReleaseScope.Chapter, Guid.NewGuid());

            Assert.IsType<BookMutationOutcome.NoChange>(result.Outcome);
        }

        [Fact]
        public async Task Release_ScopeTheEnumDoesNotName_IsRejected()
        {
            // The wire binds enums from integers too, so { "scope": 7 } reaches the handler.
            var b = await SeedAsync();

            var result = await ReleaseAsync((NarrationReleaseScope)7, b.ChapterId("ch1"));

            var rejected = Assert.IsType<BookMutationOutcome.Rejected>(result.Outcome);
            Assert.Equal(BookMutationRejection.Validation, rejected.Reason);
            Assert.Equal(ProjectDbContext.NarratorId, (await ItemAsync(b, "q-item")).CharacterId);
        }

        [Fact]
        public async Task Release_ReleasedParagraphsBecomeCharacterParagraphsWithAttributionRemaining()
        {
            var b = await SeedAsync();

            await ReleaseAsync(NarrationReleaseScope.Chapter, b.ChapterId("ch1"));

            var unprocessed = await _reader.GetCharacterParagraphsAsync(
                _folder, BookNodeLevel.Chapter, b.ChapterId("ch1"), unprocessedOnly: true);
            Assert.Equal(
                new HashSet<Guid> { b.ParagraphId("q"), b.ParagraphId("a"), b.ParagraphId("open"), b.ParagraphId("trailing") },
                unprocessed.Select(r => r.ParagraphId).ToHashSet());

            var seed = (await _reader.GetNodeStatusSeedAsync(_folder)).ToDictionary(r => r.ParagraphId);
            Assert.Equal(1, seed[b.ParagraphId("q")].Unattributed);
            Assert.Equal(1, seed[b.ParagraphId("trailing")].Unattributed);
            Assert.Equal(0, seed[b.ParagraphId("tagged")].Unattributed);
        }
    }
}
