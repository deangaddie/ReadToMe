using FractionalIndexing;
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
        ///   Vol1 > Part1 > Ch1: Narration (audio set), ChapterPause (no audio), Character (no audio)
        ///   Vol2 > Part2 > Ch2: Character (audio set), Pause (no audio)
        /// Returns ids needed for assertions.
        /// </summary>
        private async Task<SeedResult> SeedBookAsync()
        {
            await using var db = await OpenDbAsync();

            var vol1Order = Key();
            var vol1 = new Volume { Id = Guid.NewGuid(), Title = "Volume One", Order = vol1Order };
            var part1 = new Part { Id = Guid.NewGuid(), VolumeId = vol1.Id, Title = null, Order = Key() };
            var ch1 = new Chapter { Id = Guid.NewGuid(), PartId = part1.Id, Title = "Chapter One", Order = Key() };
            var para1 = new Paragraph { Id = Guid.NewGuid(), ChapterId = ch1.Id, Order = Key() };

            // item1: Narration with audio
            var item1 = new ParagraphItem
            {
                Id = Guid.NewGuid(), ParagraphId = para1.Id, Order = Key(),
                ItemType = ParagraphItemType.Narration, Text = "Narrate this",
                AudioFileName = "audio/narration.wav"
            };
            // item2: ChapterPause (Pause-kind, no audio regardless)
            var item2 = new ParagraphItem
            {
                Id = Guid.NewGuid(), ParagraphId = para1.Id, Order = Key(item1.Order),
                ItemType = ParagraphItemType.ChapterPause,
                AudioFileName = "audio/should-be-ignored.wav" // stored but must return null
            };
            // item3: Character without audio
            var item3 = new ParagraphItem
            {
                Id = Guid.NewGuid(), ParagraphId = para1.Id, Order = Key(item2.Order),
                ItemType = ParagraphItemType.Character, Text = "Dialog line"
            };

            var vol2Order = Key(vol1Order);
            var vol2 = new Volume { Id = Guid.NewGuid(), Title = "Volume Two", Order = vol2Order };
            var part2 = new Part { Id = Guid.NewGuid(), VolumeId = vol2.Id, Title = "Part Two", Order = Key() };
            var ch2 = new Chapter { Id = Guid.NewGuid(), PartId = part2.Id, Title = "Chapter Two", Order = Key() };
            var para2 = new Paragraph { Id = Guid.NewGuid(), ChapterId = ch2.Id, Order = Key() };

            // item4: Character with audio
            var item4 = new ParagraphItem
            {
                Id = Guid.NewGuid(), ParagraphId = para2.Id, Order = Key(),
                ItemType = ParagraphItemType.Character, Text = "Vol2 dialog",
                AudioFileName = "audio/character.wav"
            };
            // item5: Pause (Pause-kind, no audio)
            var item5 = new ParagraphItem
            {
                Id = Guid.NewGuid(), ParagraphId = para2.Id, Order = Key(item4.Order),
                ItemType = ParagraphItemType.Pause
            };

            db.Volumes.AddRange(vol1, vol2);
            db.Parts.AddRange(part1, part2);
            db.Chapters.AddRange(ch1, ch2);
            db.Paragraphs.AddRange(para1, para2);
            db.ParagraphItems.AddRange(item1, item2, item3, item4, item5);
            await db.SaveChangesAsync();

            return new SeedResult(
                Vol1Id: vol1.Id, Vol1Title: "Volume One",
                Part1Id: part1.Id, Part1Title: null,
                Ch1Id: ch1.Id, Ch1Title: "Chapter One",
                Vol2Id: vol2.Id, Vol2Title: "Volume Two",
                Part2Id: part2.Id, Part2Title: "Part Two",
                Ch2Id: ch2.Id, Ch2Title: "Chapter Two",
                Item1Id: item1.Id, Item2Id: item2.Id, Item3Id: item3.Id,
                Item4Id: item4.Id, Item5Id: item5.Id);
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
