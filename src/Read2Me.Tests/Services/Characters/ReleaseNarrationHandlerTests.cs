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
    /// leaves alone at each scope, and what the receipt names. A Chapter or Part release takes only
    /// speech-marked paragraphs (ADR 0015); a Paragraph release takes its paragraph whatever it says.
    /// Follows <see cref="SetParagraphsCharacterHandlerTests"/>.
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
        /// Part 1 / Chapter 1: a transcript of five <c>Q.</c> and five <c>A.</c> paragraphs (q1–q5,
        /// a1–a5; a5 ends in a pause), a quoted paragraph with a tag, a paragraph of unattributed
        /// dialog with a tag, attributed dialog, a lone pause, and three plain narration paragraphs,
        /// the last followed by a pause. Part 1 / Chapter 2 holds four <c>Q.</c> paragraphs — one short of the gate — and a
        /// plain narration paragraph; Part 2 / Chapter 3 five more <c>Q.</c> paragraphs, so every
        /// scope has releasable paragraphs outside it.
        /// </summary>
        private async Task<BookHierarchyBuilder> SeedAsync()
        {
            var b = new BookHierarchyBuilder(OpenDbAsync);
            b.WithCharacter("alice", new Character { Id = AliceId, Name = "Alice" });
            await b.AddVolume("vol", v => v
                .AddPart("part1", p => p
                    .AddChapter("ch1", c =>
                    {
                        c.AddTranscript("q", "Q.", 5);
                        c.AddTranscript("a", "A.", 4);
                        c.AddParagraph("a5", p => p
                                .AddNarration("a5-item", "A. The end.")
                                .AddPause("a5-pause"))
                            .AddParagraph("tagged", p => p
                                .AddRawItem("tagged-dialog", ParagraphItemType.Speech, "\"Hi,\"", AliceId)
                                .AddNarration("tagged-tag", "he said."))
                            .AddParagraph("open", p => p
                                .AddRawItem("open-dialog", ParagraphItemType.Speech, "\"Who?\"", null)
                                .AddNarration("open-tag", "she asked."))
                            .AddParagraph("attributed", p => p
                                .AddRawItem("attributed-dialog", ParagraphItemType.Speech, "\"Me.\"", AliceId))
                            .AddParagraph("pause", p => p.AddPause("pause-item"))
                            .AddParagraph("plain1", p => p.AddNarration("plain1-item", "The court was full."))
                            .AddParagraph("plain2", p => p.AddNarration("plain2-item", "Nobody moved."))
                            .AddParagraph("trailing", p => p
                                .AddNarration("trailing-narration", "Silence fell.")
                                .AddPause("trailing-pause"));
                    })
                    .AddChapter("ch2", c =>
                    {
                        c.AddTranscript("ch2-q", "Q.", 4);
                        c.AddParagraph("ch2-p", p => p.AddNarration("ch2-item", "Elsewhere."));
                    }))
                .AddPart("part2", p => p
                    .AddChapter("ch3", c => c.AddTranscript("ch3-q", "Q.", 5))))
                .BuildAsync();
            return b;
        }

        private static string[] Names(string prefix, int count, string suffix = "") =>
            [.. Enumerable.Range(1, count).Select(n => $"{prefix}{n}{suffix}")];

        /// <summary>The ten ch1 transcript paragraphs a Chapter or Part release of part1 takes.</summary>
        private static readonly string[] TranscriptParagraphs = [.. Names("q", 5), .. Names("a", 5)];

        private static readonly string[] TranscriptItems = [.. Names("q", 5, "-item"), .. Names("a", 5, "-item")];

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
        public async Task Release_ClearsTheNarratorStampFromSpeechMarkedParagraphs()
        {
            var b = await SeedAsync();

            await ReleaseAsync(NarrationReleaseScope.Chapter, b.ChapterId("ch1"));

            foreach (var item in TranscriptItems)
                Assert.Null((await ItemAsync(b, item)).CharacterId);
        }

        [Fact]
        public async Task Release_ChapterScope_LeavesPlainNarrationWithTheNarrator()
        {
            var b = await SeedAsync();

            await ReleaseAsync(NarrationReleaseScope.Chapter, b.ChapterId("ch1"));

            foreach (var item in new[] { "plain1-item", "plain2-item", "trailing-narration" })
                Assert.Equal(ProjectDbContext.NarratorId, (await ItemAsync(b, item)).CharacterId);
        }

        [Theory]
        [InlineData(3, false)] // a chapter's few em-dash parentheticals: keyed, but short of the gate
        [InlineData(5, true)]
        public async Task Release_EmDashSpeech_ReleasesOnlyWhenItRepeatsEnough(int count, bool released)
        {
            var b = new BookHierarchyBuilder(OpenDbAsync);
            await b.AddVolume("vol", v => v
                .AddPart("part", p => p
                    .AddChapter("ch", c =>
                    {
                        for (var n = 1; n <= count; n++)
                        {
                            var name = $"d{n}";
                            c.AddParagraph(name, p => p.AddNarration($"{name}-item", "Lucy—I call the thing by its name—saw it."));
                        }
                        c.AddParagraph("plain", p => p.AddNarration("plain-item", "The night was cold."));
                    })))
                .BuildAsync();

            var result = await ReleaseAsync(NarrationReleaseScope.Chapter, b.ChapterId("ch"));

            if (released)
                Assert.Equal(count, Assert.IsType<BookMutationOutcome.Committed>(result.Outcome).Receipt.Effects.ParagraphIds.Count);
            else
                Assert.IsType<BookMutationOutcome.NoChange>(result.Outcome);
            Assert.Equal(ProjectDbContext.NarratorId, (await ItemAsync(b, "plain-item")).CharacterId);
        }

        [Fact]
        public async Task Release_ChapterScope_AMarkerShortOfTheGate_IsNoChange()
        {
            var b = await SeedAsync();

            var result = await ReleaseAsync(NarrationReleaseScope.Chapter, b.ChapterId("ch2"));

            Assert.IsType<BookMutationOutcome.NoChange>(result.Outcome);
            Assert.Equal(ProjectDbContext.NarratorId, (await ItemAsync(b, "ch2-q1-item")).CharacterId);
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
            Assert.Equal(ParagraphItemType.Pause, (await ItemAsync(b, "a5-pause")).ItemType);
        }

        [Fact]
        public async Task Release_KeepsTextBoundariesAndVoiceInstructions()
        {
            var b = await SeedAsync();
            await using (var db = await OpenDbAsync())
            {
                (await db.ParagraphItems.FindAsync(b.ItemId("q1-item")))!.VoiceInstructions = "stern";
                await db.SaveChangesAsync();
            }

            await ReleaseAsync(NarrationReleaseScope.Chapter, b.ChapterId("ch1"));

            var q = await ItemAsync(b, "q1-item");
            Assert.Equal("Q. Line q1.", q.Text);
            Assert.Equal("stern", q.VoiceInstructions);
            Assert.Equal(ParagraphItemType.Speech, q.ItemType);
            await using var verify = await OpenDbAsync();
            Assert.Equal(2, verify.ParagraphItems.Count(i => i.ParagraphId == b.ParagraphId("a5")));
        }

        [Fact]
        public async Task Release_DropsAudioOnlyFromReleasedItems()
        {
            var b = await SeedAsync();
            await SetAudioAsync(b, "q1-item", "tagged-tag", "tagged-dialog", "trailing-narration", "ch2-item");

            var result = await ReleaseAsync(NarrationReleaseScope.Chapter, b.ChapterId("ch1"));

            Assert.Null((await ItemAsync(b, "q1-item")).AudioFileName);
            Assert.NotNull((await ItemAsync(b, "tagged-tag")).AudioFileName);
            Assert.NotNull((await ItemAsync(b, "tagged-dialog")).AudioFileName);
            Assert.NotNull((await ItemAsync(b, "trailing-narration")).AudioFileName);
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
                TranscriptParagraphs.Select(b.ParagraphId).ToHashSet(),
                receipt.Effects.ParagraphIds.ToHashSet());
            Assert.Equal(
                TranscriptItems.Select(b.ItemId).ToHashSet(),
                receipt.Effects.ParagraphItemIds.ToHashSet());
            Assert.Null(result.EntityId);
        }

        [Fact]
        public async Task Release_ChapterScope_LeavesOtherChaptersAlone()
        {
            var b = await SeedAsync();

            await ReleaseAsync(NarrationReleaseScope.Chapter, b.ChapterId("ch1"));

            Assert.Equal(ProjectDbContext.NarratorId, (await ItemAsync(b, "ch3-q1-item")).CharacterId);
        }

        [Fact]
        public async Task Release_PartScope_GatesEachChapterOnItsOwnMarkers()
        {
            // ch1 has Q. ×5 and ch2 Q. ×4: nine across the Part, but ch2 alone is short of the gate.
            var b = await SeedAsync();

            var result = await ReleaseAsync(NarrationReleaseScope.Part, b.PartId("part1"));

            var receipt = Assert.IsType<BookMutationOutcome.Committed>(result.Outcome).Receipt;
            Assert.Equal(
                TranscriptParagraphs.Select(b.ParagraphId).ToHashSet(),
                receipt.Effects.ParagraphIds.ToHashSet());
            Assert.Equal(ProjectDbContext.NarratorId, (await ItemAsync(b, "ch2-q1-item")).CharacterId);
            Assert.Equal(ProjectDbContext.NarratorId, (await ItemAsync(b, "ch2-item")).CharacterId);
            Assert.Equal(ProjectDbContext.NarratorId, (await ItemAsync(b, "ch3-q1-item")).CharacterId);
        }

        [Fact]
        public async Task Release_ParagraphScope_ReachesOnlyThatParagraph()
        {
            var b = await SeedAsync();

            await ReleaseAsync(NarrationReleaseScope.Paragraph, b.ParagraphId("q1"));

            Assert.Null((await ItemAsync(b, "q1-item")).CharacterId);
            Assert.Equal(ProjectDbContext.NarratorId, (await ItemAsync(b, "q2-item")).CharacterId);
            Assert.Equal(ProjectDbContext.NarratorId, (await ItemAsync(b, "ch2-item")).CharacterId);
        }

        [Fact]
        public async Task Release_ParagraphScope_ReleasesPlainNarrationUnfiltered()
        {
            var b = await SeedAsync();

            var result = await ReleaseAsync(NarrationReleaseScope.Paragraph, b.ParagraphId("trailing"));

            Assert.IsType<BookMutationOutcome.Committed>(result.Outcome);
            Assert.Null((await ItemAsync(b, "trailing-narration")).CharacterId);
        }

        [Fact]
        public async Task Release_ParagraphScope_OnAQuotedParagraph_IsNoChange()
        {
            var b = await SeedAsync();

            var result = await ReleaseAsync(NarrationReleaseScope.Paragraph, b.ParagraphId("tagged"));

            Assert.IsType<BookMutationOutcome.NoChange>(result.Outcome);
            Assert.Equal(ProjectDbContext.NarratorId, (await ItemAsync(b, "tagged-tag")).CharacterId);
        }

        [Theory]
        [InlineData("Seldon", null, true)]          // a Character's name
        [InlineData("Hari Seldon", "Seldon", true)] // an alias
        [InlineData("Gaal Dornick", null, false)]   // on the roster, but not the prefix
        public async Task Release_NamePrefix_ReleasesOnlyARosterName(string name, string? alias, bool released)
        {
            var b = new BookHierarchyBuilder(OpenDbAsync);
            var character = new Character { Id = Guid.NewGuid(), Name = name };
            if (alias is not null) character.Aliases.Add(new CharacterAlias { Id = Guid.NewGuid(), Name = alias });
            b.WithCharacter("speaker", character);
            await b.AddVolume("vol", v => v
                .AddPart("part", p => p
                    .AddChapter("ch", c => c.AddTranscript("s", "Seldon:", 5))))
                .BuildAsync();

            var result = await ReleaseAsync(NarrationReleaseScope.Chapter, b.ChapterId("ch"));

            if (released)
                Assert.Equal(5, Assert.IsType<BookMutationOutcome.Committed>(result.Outcome).Receipt.Effects.ParagraphIds.Count);
            else
                Assert.IsType<BookMutationOutcome.NoChange>(result.Outcome);
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
            Assert.Equal(ProjectDbContext.NarratorId, (await ItemAsync(b, "q1-item")).CharacterId);
        }

        [Fact]
        public async Task Release_ReleasedParagraphsBecomeCharacterParagraphsWithAttributionRemaining()
        {
            var b = await SeedAsync();

            await ReleaseAsync(NarrationReleaseScope.Chapter, b.ChapterId("ch1"));

            var unprocessed = await _reader.GetCharacterParagraphsAsync(
                _folder, BookNodeLevel.Chapter, b.ChapterId("ch1"), unprocessedOnly: true);
            Assert.Equal(
                TranscriptParagraphs.Append("open").Select(b.ParagraphId).ToHashSet(),
                unprocessed.Select(r => r.ParagraphId).ToHashSet());

            var seed = (await _reader.GetNodeStatusSeedAsync(_folder)).ToDictionary(r => r.ParagraphId);
            Assert.Equal(1, seed[b.ParagraphId("q1")].Unattributed);
            Assert.Equal(0, seed[b.ParagraphId("trailing")].Unattributed);
            Assert.Equal(0, seed[b.ParagraphId("tagged")].Unattributed);
        }
    }
}
