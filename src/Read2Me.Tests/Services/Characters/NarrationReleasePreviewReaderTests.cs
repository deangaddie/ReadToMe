using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Read2Me.Core.Configuration;
using Read2Me.Core.Models;
using Read2Me.Data;
using Read2Me.Data.Entities;
using Read2Me.Data.Enums;
using Read2Me.Services;
using Read2Me.Services.Characters;
using Read2Me.Services.Commands.Handlers;
using Read2Me.Services.IO;
using Read2Me.Services.Mutations;
using Read2Me.Tests.Infrastructure;
using Xunit;

namespace Read2Me.Tests.Services.Characters
{
    /// <summary>
    /// The Release narration confirm's figures come from a read, as the bulk-assign confirm's do
    /// (<see cref="BulkAssignPreviewReaderTests"/>): a Part can cover chapters that were never
    /// expanded. They must agree with what the command then writes.
    /// </summary>
    public class NarrationReleasePreviewReaderTests : ProjectDbTestBase
    {
        private readonly ProjectReader _reader;
        private readonly ServiceProvider _root;
        private readonly ProjectFolderId _folder;

        private static readonly Guid AliceId = Guid.NewGuid();

        public NarrationReleasePreviewReaderTests()
        {
            var fs = new FileSystemService(Options.Create(new WorkspaceOptions { FolderPath = TempDir }));
            var session = new ProjectDbSession(fs, new ProjectDbContextProvider(), NullLogger<ProjectDbSession>.Instance);
            _reader = new ProjectReader(session, NullLogger<ProjectReader>.Instance);
            _folder = new ProjectFolderId(FolderName);

            var services = new ServiceCollection();
            services.AddBookCommandHandlers();
            services.Configure<WorkspaceOptions>(o => o.FolderPath = TempDir);
            services.AddSingleton<IProjectDbContextFactory, ProjectDbContextProvider>();
            _root = services.BuildServiceProvider();
        }

        public override async ValueTask DisposeAsync()
        {
            await _root.DisposeAsync();
            await base.DisposeAsync();
        }

        /// <summary>
        /// ch1: q1–q5 (<c>Q.</c>, q1 with audio), a1–a4 (<c>A.</c>), a5 (<c>A.</c> then a second
        /// narration item with audio), tagged (dialog + narration tag with audio), pause, and plain
        /// narration with audio. ch2 (same part): four <c>Q.</c> paragraphs, one short of the gate,
        /// q1 with audio. part2/ch3: one plain narration paragraph.
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
                                .AddNarration("a5-item-1", "A. Hari Seldon.")
                                .AddNarration("a5-item-2", "A mathematician."))
                            .AddParagraph("tagged", p => p
                                .AddRawItem("tagged-dialog", ParagraphItemType.Speech, "\"Hi,\"", AliceId)
                                .AddNarration("tagged-tag", "he said."))
                            .AddParagraph("pause", p => p.AddPause("pause-item"))
                            .AddParagraph("plain", p => p.AddNarration("plain-item", "Silence fell."));
                    })
                    .AddChapter("ch2", c => c.AddTranscript("ch2-q", "Q.", 4)))
                .AddPart("part2", p => p
                    .AddChapter("ch3", c => c
                        .AddParagraph("ch3-p", p => p.AddNarration("ch3-item", "Later.")))))
                .BuildAsync();

            await using var db = await OpenDbAsync();
            foreach (var name in new[] { "q1-item", "a5-item-2", "tagged-tag", "plain-item", "ch2-q1-item" })
                (await db.ParagraphItems.FindAsync(b.ItemId(name)))!.AudioFileName = $"audio/{name}.wav";
            await db.SaveChangesAsync();
            return b;
        }

        [Theory]
        [InlineData(NarrationReleaseScope.Chapter, "ch1", 10, 2)]
        [InlineData(NarrationReleaseScope.Part, "part1", 10, 2)]   // ch2's four Q. are short of the gate
        [InlineData(NarrationReleaseScope.Chapter, "ch2", 0, 0)]
        [InlineData(NarrationReleaseScope.Paragraph, "a5", 1, 1)]
        [InlineData(NarrationReleaseScope.Paragraph, "plain", 1, 1)] // a Paragraph release is unfiltered
        [InlineData(NarrationReleaseScope.Paragraph, "tagged", 0, 0)]
        [InlineData(NarrationReleaseScope.Paragraph, "pause", 0, 0)]
        public async Task Preview_CountsSpeechMarkedParagraphsAndTheirItemsWithAudio(
            NarrationReleaseScope scope, string node, int paragraphs, int itemsWithAudio)
        {
            var b = await SeedAsync();

            var preview = await _reader.GetNarrationReleasePreviewAsync(_folder, scope, NodeId(b, scope, node));

            Assert.Equal(paragraphs, preview.Paragraphs);
            Assert.Equal(itemsWithAudio, preview.ItemsWithAudio);
        }

        [Fact]
        public async Task Preview_UnknownNode_IsZeroes()
        {
            await SeedAsync();

            var preview = await _reader.GetNarrationReleasePreviewAsync(
                _folder, NarrationReleaseScope.Chapter, Guid.NewGuid());

            Assert.Equal(0, preview.Paragraphs);
            Assert.Equal(0, preview.ItemsWithAudio);
            Assert.Empty(preview.SpeechMarkers);
        }

        [Theory]
        [InlineData(NarrationReleaseScope.Chapter, "ch1", "A.=5,Q.=5")]   // count descending, then marker
        [InlineData(NarrationReleaseScope.Part, "part1", "A.=5,Q.=5")]    // ch2's four Q. pass nothing
        [InlineData(NarrationReleaseScope.Chapter, "ch2", "")]
        [InlineData(NarrationReleaseScope.Chapter, "ch3", "")]
        [InlineData(NarrationReleaseScope.Paragraph, "q1", "")]           // a Paragraph release names no markers
        [InlineData(NarrationReleaseScope.Paragraph, "plain", "")]
        public async Task Preview_NamesThePassingSpeechMarkers(NarrationReleaseScope scope, string node, string expected)
        {
            var b = await SeedAsync();

            var preview = await _reader.GetNarrationReleasePreviewAsync(_folder, scope, NodeId(b, scope, node));

            Assert.Equal(expected, string.Join(',', preview.SpeechMarkers.Select(m => $"{m.Marker}={m.Paragraphs}")));
        }

        [Theory]
        [InlineData(NarrationReleaseScope.Chapter, "ch1")]
        [InlineData(NarrationReleaseScope.Part, "part1")]
        [InlineData(NarrationReleaseScope.Paragraph, "plain")]
        public async Task Preview_AgreesWithWhatTheCommandWrites(NarrationReleaseScope scope, string node)
        {
            var b = await SeedAsync();
            var nodeId = NodeId(b, scope, node);
            var preview = await _reader.GetNarrationReleasePreviewAsync(_folder, scope, nodeId);

            List<(Guid Id, bool HadAudio)> before;
            await using (var db = await OpenDbAsync())
                before = db.ParagraphItems.Select(i => new { i.Id, HadAudio = i.AudioFileName != null })
                    .AsEnumerable().Select(i => (i.Id, i.HadAudio)).ToList();

            var handler = new ReleaseNarrationHandler(_root.GetRequiredService<BookMutations>());
            var result = await handler.HandleAsync(new ReleaseNarrationCommand(_folder, scope, nodeId), CancellationToken.None);

            var effects = Assert.IsType<BookMutationOutcome.Committed>(result.Outcome).Receipt.Effects;
            var released = effects.ParagraphItemIds.ToHashSet();
            Assert.Equal(preview.Paragraphs, effects.ParagraphIds.Count);
            Assert.Equal(preview.ItemsWithAudio, before.Count(i => released.Contains(i.Id) && i.HadAudio));
            if (scope == NarrationReleaseScope.Paragraph)
                Assert.Empty(preview.SpeechMarkers);
            else
                Assert.Equal(effects.ParagraphIds.Count, preview.SpeechMarkers.Sum(m => m.Paragraphs));

            // And afterwards nothing is left to release.
            var after = await _reader.GetNarrationReleasePreviewAsync(_folder, scope, nodeId);
            Assert.Equal(0, after.Paragraphs);
            Assert.Empty(after.SpeechMarkers);
        }

        [Fact]
        public async Task Preview_PartMarkersAreSummedAcrossItsChaptersAndAgreeWithTheWrite()
        {
            // chA: Q. ×5, A. ×6; chB: Q. ×5, A. ×4 — chB's A. is short of its own chapter's gate.
            var b = new BookHierarchyBuilder(OpenDbAsync);
            await b.AddVolume("vol", v => v
                .AddPart("part", p => p
                    .AddChapter("chA", c =>
                    {
                        c.AddTranscript("aq", "Q.", 5);
                        c.AddTranscript("aa", "A.", 6);
                    })
                    .AddChapter("chB", c =>
                    {
                        c.AddTranscript("bq", "Q.", 5);
                        c.AddTranscript("ba", "A.", 4);
                    })))
                .BuildAsync();
            var partId = b.PartId("part");

            var preview = await _reader.GetNarrationReleasePreviewAsync(_folder, NarrationReleaseScope.Part, partId);

            Assert.Equal([new SpeechMarkerCount("Q.", 10), new SpeechMarkerCount("A.", 6)], preview.SpeechMarkers);

            var handler = new ReleaseNarrationHandler(_root.GetRequiredService<BookMutations>());
            var result = await handler.HandleAsync(
                new ReleaseNarrationCommand(_folder, NarrationReleaseScope.Part, partId), CancellationToken.None);
            var effects = Assert.IsType<BookMutationOutcome.Committed>(result.Outcome).Receipt.Effects;
            Assert.Equal(16, effects.ParagraphIds.Count);
            Assert.Equal(preview.Paragraphs, effects.ParagraphIds.Count);
        }

        [Fact]
        public async Task Selection_PassingMarkersAreSummedAcrossThePartsChapters()
        {
            // chA: Q. ×5, A. ×6; chB: Q. ×5, A. ×4 — chB's A. is short of its own chapter's gate.
            var b = new BookHierarchyBuilder(OpenDbAsync);
            await b.AddVolume("vol", v => v
                .AddPart("part", p => p
                    .AddChapter("chA", c =>
                    {
                        c.AddTranscript("aq", "Q.", 5);
                        c.AddTranscript("aa", "A.", 6);
                    })
                    .AddChapter("chB", c =>
                    {
                        c.AddTranscript("bq", "Q.", 5);
                        c.AddTranscript("ba", "A.", 4);
                    })))
                .BuildAsync();
            await using var db = await OpenDbAsync();

            var part = await NarrationRelease.SelectAsync(db, NarrationReleaseScope.Part, b.PartId("part"));
            var paragraph = await NarrationRelease.SelectAsync(db, NarrationReleaseScope.Paragraph, b.ParagraphId("aq1"));

            Assert.Equal([new SpeechMarkerCount("Q.", 10), new SpeechMarkerCount("A.", 6)], part.Markers);
            Assert.Equal(16, part.Paragraphs.Count);
            Assert.Single(paragraph.Paragraphs);
            Assert.Empty(paragraph.Markers);
        }

        private static Guid NodeId(BookHierarchyBuilder b, NarrationReleaseScope scope, string node) => scope switch
        {
            NarrationReleaseScope.Part => b.PartId(node),
            NarrationReleaseScope.Chapter => b.ChapterId(node),
            _ => b.ParagraphId(node),
        };
    }
}
