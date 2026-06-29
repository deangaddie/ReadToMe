using FractionalIndexing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Read2Me.Core.Configuration;
using Read2Me.Core.Models;
using Read2Me.Data.Entities;
using Read2Me.Data.Enums;
using Read2Me.Services;
using Read2Me.Services.IO;
using Read2Me.Tests.Infrastructure;
using Xunit;

namespace Read2Me.Tests.Services
{
    public class ProjectReaderAssemblyManifestTests : ProjectDbTestBase
    {
        private readonly ProjectReader _reader;
        private readonly ProjectFolderId _folder;

        public ProjectReaderAssemblyManifestTests()
        {
            var fs = new FileSystemService(Options.Create(new WorkspaceOptions { FolderPath = TempDir }));
            var session = new ProjectDbSession(fs, new ProjectDbContextProvider(), NullLogger<ProjectDbSession>.Instance);
            _reader = new ProjectReader(session, NullLogger<ProjectReader>.Instance);
            _folder = new ProjectFolderId(FolderName);
        }

        private static string Key(string? prev = null) => OrderKeyGenerator.GenerateKeyBetween(prev, null);

        /// <summary>
        /// Seeds a 2-volume book:
        ///   Vol1 > Part1 (no title) > Ch1: Narration (audio set), ChapterPause (no audio), Character (no audio)
        ///   Vol2 > Part2 ("Part Two") > Ch2: Character (audio set), Pause (no audio)
        /// Returns ids needed for assertions.
        /// </summary>
        private async Task<SeedResult> SeedBookAsync()
        {
            // Builder creates the spine (vol/part/ch/para). Items seeded post-build for AudioFileName control.
            var b = new BookHierarchyBuilder(OpenDbAsync);
            await b
                .AddVolume("vol1", v => v.AddPart(configure: p => p.AddChapter("ch1", c => c.AddParagraph("para1"))))
                .AddVolume("vol2", v => v.AddPart("Part Two", p => p.AddChapter("ch2", c => c.AddParagraph("para2"))))
                .BuildAsync();

            // Update volume and chapter titles to match original seed expectations
            Guid item1Id, item2Id, item3Id, item4Id, item5Id;
            Guid vol1Id = b.VolumeId("vol1"), vol2Id = b.VolumeId("vol2");
            Guid ch1Id = b.ChapterId("ch1"), ch2Id = b.ChapterId("ch2");
            Guid part2Id = b.PartId("Part Two");

            await using var db = await OpenDbAsync();

            // Set chapter and volume titles
            var vol1 = await db.Volumes.FindAsync(vol1Id);
            vol1!.Title = "Volume One";
            var vol2 = await db.Volumes.FindAsync(vol2Id);
            vol2!.Title = "Volume Two";
            var ch1 = await db.Chapters.FindAsync(ch1Id);
            ch1!.Title = "Chapter One";
            var ch2 = await db.Chapters.FindAsync(ch2Id);
            ch2!.Title = "Chapter Two";

            // Retrieve part1 (implicit — no name registered; look up via vol1)
            var part1 = await db.Parts.FirstAsync(p => p.VolumeId == vol1Id);
            var part1Id = part1.Id;
            // part1.Title is already null (implicit part has no name)

            // Seed the 5 items
            string? order = null;
            item1Id = Guid.NewGuid();
            db.ParagraphItems.Add(new ParagraphItem
            {
                Id = item1Id, ParagraphId = b.ParagraphId("para1"), Order = order = Key(order),
                ItemType = ParagraphItemType.Narration, Text = "Narrate this",
                AudioFileName = "audio/narration.wav"
            });
            item2Id = Guid.NewGuid();
            db.ParagraphItems.Add(new ParagraphItem
            {
                Id = item2Id, ParagraphId = b.ParagraphId("para1"), Order = order = Key(order),
                ItemType = ParagraphItemType.ChapterPause,
                AudioFileName = "audio/should-be-ignored.wav"
            });
            item3Id = Guid.NewGuid();
            db.ParagraphItems.Add(new ParagraphItem
            {
                Id = item3Id, ParagraphId = b.ParagraphId("para1"), Order = order = Key(order),
                ItemType = ParagraphItemType.Character, Text = "Dialog line"
            });

            string? order2 = null;
            item4Id = Guid.NewGuid();
            db.ParagraphItems.Add(new ParagraphItem
            {
                Id = item4Id, ParagraphId = b.ParagraphId("para2"), Order = order2 = Key(order2),
                ItemType = ParagraphItemType.Character, Text = "Vol2 dialog",
                AudioFileName = "audio/character.wav"
            });
            item5Id = Guid.NewGuid();
            db.ParagraphItems.Add(new ParagraphItem
            {
                Id = item5Id, ParagraphId = b.ParagraphId("para2"), Order = order2 = Key(order2),
                ItemType = ParagraphItemType.Pause
            });

            await db.SaveChangesAsync();

            return new SeedResult(
                Vol1Id: vol1Id, Vol1Title: "Volume One",
                Part1Id: part1Id, Part1Title: null,
                Ch1Id: ch1Id, Ch1Title: "Chapter One",
                Vol2Id: vol2Id, Vol2Title: "Volume Two",
                Part2Id: part2Id, Part2Title: "Part Two",
                Ch2Id: ch2Id, Ch2Title: "Chapter Two",
                Item1Id: item1Id, Item2Id: item2Id, Item3Id: item3Id,
                Item4Id: item4Id, Item5Id: item5Id);
        }

        private sealed record SeedResult(
            Guid Vol1Id, string Vol1Title,
            Guid Part1Id, string? Part1Title,
            Guid Ch1Id, string Ch1Title,
            Guid Vol2Id, string Vol2Title,
            Guid Part2Id, string Part2Title,
            Guid Ch2Id, string Ch2Title,
            Guid Item1Id, Guid Item2Id, Guid Item3Id,
            Guid Item4Id, Guid Item5Id);

        [Fact]
        public async Task GetAssemblyManifest_ReturnsAllItemsInPositionOrder()
        {
            var s = await SeedBookAsync();
            var manifest = await _reader.GetAssemblyManifestAsync(_folder, CancellationToken.None);

            Assert.Equal(5, manifest.Count);
            Assert.Equal(s.Item1Id, manifest[0].ParagraphItemId);
            Assert.Equal(s.Item2Id, manifest[1].ParagraphItemId);
            Assert.Equal(s.Item3Id, manifest[2].ParagraphItemId);
            Assert.Equal(s.Item4Id, manifest[3].ParagraphItemId);
            Assert.Equal(s.Item5Id, manifest[4].ParagraphItemId);
        }

        [Fact]
        public async Task GetAssemblyManifest_PauseKindItems_HaveNullAudioPath()
        {
            var s = await SeedBookAsync();
            var manifest = await _reader.GetAssemblyManifestAsync(_folder, CancellationToken.None);

            var pauseEntry = manifest.Single(e => e.ParagraphItemId == s.Item2Id);
            Assert.Equal(ParagraphItemType.ChapterPause, pauseEntry.ItemType);
            Assert.Null(pauseEntry.AudioRelativePath);

            var pauseEntry2 = manifest.Single(e => e.ParagraphItemId == s.Item5Id);
            Assert.Equal(ParagraphItemType.Pause, pauseEntry2.ItemType);
            Assert.Null(pauseEntry2.AudioRelativePath);
        }

        [Fact]
        public async Task GetAssemblyManifest_NonPauseItems_CarryStoredAudioPath()
        {
            var s = await SeedBookAsync();
            var manifest = await _reader.GetAssemblyManifestAsync(_folder, CancellationToken.None);

            var narration = manifest.Single(e => e.ParagraphItemId == s.Item1Id);
            Assert.Equal("audio/narration.wav", narration.AudioRelativePath);

            var noAudio = manifest.Single(e => e.ParagraphItemId == s.Item3Id);
            Assert.Null(noAudio.AudioRelativePath);

            var withAudio = manifest.Single(e => e.ParagraphItemId == s.Item4Id);
            Assert.Equal("audio/character.wav", withAudio.AudioRelativePath);
        }

        [Fact]
        public async Task GetAssemblyManifest_EntriesCarrySectionAncestry()
        {
            var s = await SeedBookAsync();
            var manifest = await _reader.GetAssemblyManifestAsync(_folder, CancellationToken.None);

            // Vol1 items
            var entry1 = manifest.Single(e => e.ParagraphItemId == s.Item1Id);
            Assert.Equal(s.Vol1Id, entry1.VolumeId);
            Assert.Equal(s.Vol1Title, entry1.VolumeTitle);
            Assert.Equal(s.Part1Id, entry1.PartId);
            Assert.Equal(s.Part1Title, entry1.PartTitle); // null
            Assert.Equal(s.Ch1Id, entry1.ChapterId);
            Assert.Equal(s.Ch1Title, entry1.ChapterTitle);

            // Vol2 items
            var entry4 = manifest.Single(e => e.ParagraphItemId == s.Item4Id);
            Assert.Equal(s.Vol2Id, entry4.VolumeId);
            Assert.Equal(s.Vol2Title, entry4.VolumeTitle);
            Assert.Equal(s.Part2Id, entry4.PartId);
            Assert.Equal(s.Part2Title, entry4.PartTitle);
            Assert.Equal(s.Ch2Id, entry4.ChapterId);
            Assert.Equal(s.Ch2Title, entry4.ChapterTitle);
        }
    }
}
