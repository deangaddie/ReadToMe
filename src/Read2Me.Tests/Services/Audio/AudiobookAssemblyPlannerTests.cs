using Read2Me.Data.Enums;
using Read2Me.Services;
using Read2Me.Services.Audio;
using Read2Me.Services.Audio.Assembly;
using Xunit;

namespace Read2Me.Tests.Services.Audio
{
    public class AudiobookAssemblyPlannerTests
    {
        private static AudioProcessingSettings DefaultSettings() => new(
            FfmpegPath: null,
            WerThreshold: 0.15,
            SentenceSplitEnabled: false,
            ChunkPauseMs: 300,
            VolumePauseMs: 4000,
            PartPauseMs: 3000,
            ChapterPauseMs: 2500,
            ParagraphPauseMs: 800,
            PauseMs: 500);

        // ── PauseMs ──────────────────────────────────────────────────────────

        [Theory]
        [InlineData(ParagraphItemType.VolumePause, 4000)]
        [InlineData(ParagraphItemType.PartPause, 3000)]
        [InlineData(ParagraphItemType.ChapterPause, 2500)]
        [InlineData(ParagraphItemType.ParagraphPause, 800)]
        [InlineData(ParagraphItemType.Pause, 500)]
        public void PauseMs_ReturnsConfiguredMs(ParagraphItemType kind, int expected)
        {
            Assert.Equal(expected, AudiobookAssemblyPlanner.PauseMs(kind, DefaultSettings()));
        }

        [Theory]
        [InlineData(ParagraphItemType.Narration)]
        [InlineData(ParagraphItemType.Character)]
        public void PauseMs_NonPauseKind_Throws(ParagraphItemType kind)
        {
            Assert.Throws<ArgumentException>(() =>
                AudiobookAssemblyPlanner.PauseMs(kind, DefaultSettings()));
        }

        // ── BuildConcatEntries ────────────────────────────────────────────────

        private static AssemblyManifestEntry AudioEntry(Guid id, string path,
            Guid? volId = null, Guid? partId = null, Guid? chapId = null) =>
            new(id, ParagraphItemType.Narration, path,
                volId ?? Guid.NewGuid(), null,
                partId ?? Guid.NewGuid(), null,
                chapId ?? Guid.NewGuid(), null);

        private static AssemblyManifestEntry PauseEntry(ParagraphItemType kind,
            Guid? volId = null, Guid? partId = null, Guid? chapId = null) =>
            new(Guid.NewGuid(), kind, null,
                volId ?? Guid.NewGuid(), null,
                partId ?? Guid.NewGuid(), null,
                chapId ?? Guid.NewGuid(), null);

        [Fact]
        public void BuildConcatEntries_AudioOnly_YieldsAudioEntries()
        {
            var id1 = Guid.NewGuid();
            var id2 = Guid.NewGuid();
            var manifest = new List<AssemblyManifestEntry>
            {
                AudioEntry(id1, "audio/a.wav"),
                AudioEntry(id2, "audio/b.wav"),
            };

            var entries = AudiobookAssemblyPlanner.BuildConcatEntries(manifest, DefaultSettings());

            Assert.Equal(2, entries.Count);
            var a = Assert.IsType<ConcatEntry.Audio>(entries[0]);
            Assert.Equal("audio/a.wav", a.RelativePath);
            var b = Assert.IsType<ConcatEntry.Audio>(entries[1]);
            Assert.Equal("audio/b.wav", b.RelativePath);
        }

        [Fact]
        public void BuildConcatEntries_PauseInterleaved_YieldsSilenceWithCorrectMs()
        {
            var manifest = new List<AssemblyManifestEntry>
            {
                AudioEntry(Guid.NewGuid(), "a.wav"),
                PauseEntry(ParagraphItemType.ChapterPause),
                AudioEntry(Guid.NewGuid(), "b.wav"),
            };

            var entries = AudiobookAssemblyPlanner.BuildConcatEntries(manifest, DefaultSettings());

            Assert.Equal(3, entries.Count);
            Assert.IsType<ConcatEntry.Audio>(entries[0]);
            var silence = Assert.IsType<ConcatEntry.Silence>(entries[1]);
            Assert.Equal(2500, silence.Milliseconds);
            Assert.IsType<ConcatEntry.Audio>(entries[2]);
        }

        [Fact]
        public void BuildConcatEntries_NullAudioPath_Throws()
        {
            var bad = new AssemblyManifestEntry(
                Guid.NewGuid(), ParagraphItemType.Narration, null,
                Guid.NewGuid(), null, Guid.NewGuid(), null, Guid.NewGuid(), null);

            var manifest = new List<AssemblyManifestEntry> { bad };

            Assert.Throws<InvalidOperationException>(() =>
                AudiobookAssemblyPlanner.BuildConcatEntries(manifest, DefaultSettings()));
        }

        // ── ComputeChapterTimestamps ──────────────────────────────────────────

        [Fact]
        public void ComputeChapterTimestamps_SingleChapter_StartZeroEndTotal()
        {
            var volId = Guid.NewGuid();
            var partId = Guid.NewGuid();
            var chapId = Guid.NewGuid();
            var itemId = Guid.NewGuid();

            var manifest = new List<AssemblyManifestEntry>
            {
                new(itemId, ParagraphItemType.Narration, "a.wav",
                    volId, "Vol 1", partId, "Part 1", chapId, "Chapter One"),
            };
            var durations = new Dictionary<Guid, TimeSpan>
            {
                [itemId] = TimeSpan.FromSeconds(10),
            };

            var chapters = AudiobookAssemblyPlanner.ComputeChapterTimestamps(manifest, durations, DefaultSettings());

            // Volume + Part + Chapter each emit at offset 0
            Assert.Equal(3, chapters.Count);
            Assert.All(chapters, c => Assert.Equal(TimeSpan.Zero, c.Start));
            Assert.All(chapters, c => Assert.Equal(TimeSpan.FromSeconds(10), c.End));
        }

        [Fact]
        public void ComputeChapterTimestamps_TwoChapters_SecondStartsAfterFirst()
        {
            var volId = Guid.NewGuid();
            var partId = Guid.NewGuid();
            var chap1Id = Guid.NewGuid();
            var chap2Id = Guid.NewGuid();
            var item1Id = Guid.NewGuid();
            var pause1Id = Guid.NewGuid();
            var item2Id = Guid.NewGuid();

            var settings = DefaultSettings();

            var manifest = new List<AssemblyManifestEntry>
            {
                new(item1Id, ParagraphItemType.Narration, "a.wav",
                    volId, "Vol 1", partId, "Part 1", chap1Id, "Chapter One"),
                new(pause1Id, ParagraphItemType.ChapterPause, null,
                    volId, "Vol 1", partId, "Part 1", chap1Id, "Chapter One"),
                new(item2Id, ParagraphItemType.Narration, "b.wav",
                    volId, "Vol 1", partId, "Part 1", chap2Id, "Chapter Two"),
            };
            var durations = new Dictionary<Guid, TimeSpan>
            {
                [item1Id] = TimeSpan.FromSeconds(5),
                [item2Id] = TimeSpan.FromSeconds(7),
            };

            var chapters = AudiobookAssemblyPlanner.ComputeChapterTimestamps(manifest, durations, settings);

            // Vol+Part+Chap1 at 0, Chap2 at 5s + 2.5s pause = 7.5s
            var chap2Markers = chapters.FindAll(c => c.Title == "Chapter Two");
            Assert.Single(chap2Markers);
            Assert.Equal(TimeSpan.FromMilliseconds(7500), chap2Markers[0].Start);

            var totalMs = 5000 + 2500 + 7000; // 14500ms
            Assert.Equal(TimeSpan.FromMilliseconds(totalMs), chap2Markers[0].End);
        }

        [Fact]
        public void ComputeChapterTimestamps_NullTitle_FallsBackToChapterN()
        {
            var volId = Guid.NewGuid();
            var partId = Guid.NewGuid();
            var chapId = Guid.NewGuid();
            var itemId = Guid.NewGuid();

            var manifest = new List<AssemblyManifestEntry>
            {
                new(itemId, ParagraphItemType.Narration, "a.wav",
                    volId, null, partId, null, chapId, null),
            };
            var durations = new Dictionary<Guid, TimeSpan> { [itemId] = TimeSpan.FromSeconds(1) };

            var chapters = AudiobookAssemblyPlanner.ComputeChapterTimestamps(manifest, durations, DefaultSettings());

            Assert.Equal(3, chapters.Count);
            Assert.Contains(chapters, c => c.Title == "Chapter 1");
        }

        // ── GenerateFfmetadata ────────────────────────────────────────────────

        [Fact]
        public void GenerateFfmetadata_ContainsHeader()
        {
            var text = AudiobookAssemblyPlanner.GenerateFfmetadata(
                "My Book", "Jane Author", new List<ChapterMarker>());

            Assert.StartsWith(";FFMETADATA1", text);
        }

        [Fact]
        public void GenerateFfmetadata_ContainsFiveGlobalTags()
        {
            var text = AudiobookAssemblyPlanner.GenerateFfmetadata(
                "My Book", "Jane Author", new List<ChapterMarker>());

            Assert.Contains("title=My Book", text);
            Assert.Contains("artist=Jane Author", text);
            Assert.Contains("album_artist=Jane Author", text);
            Assert.Contains("album=My Book", text);
            Assert.Contains("genre=Audiobook", text);
        }

        [Fact]
        public void GenerateFfmetadata_ChapterBlock_CorrectFormat()
        {
            var markers = new List<ChapterMarker>
            {
                new("Chapter One", TimeSpan.FromMilliseconds(0), TimeSpan.FromMilliseconds(5000)),
            };

            var text = AudiobookAssemblyPlanner.GenerateFfmetadata("Book", "Author", markers);

            Assert.Contains("[CHAPTER]", text);
            Assert.Contains("TIMEBASE=1/1000", text);
            Assert.Contains("START=0", text);
            Assert.Contains("END=5000", text);
            Assert.Contains("title=Chapter One", text);
        }

        [Fact]
        public void GenerateFfmetadata_EscapesSpecialChars()
        {
            var markers = new List<ChapterMarker>
            {
                new("Title=with;special#chars\\and\nnewline",
                    TimeSpan.Zero, TimeSpan.FromSeconds(1)),
            };

            var text = AudiobookAssemblyPlanner.GenerateFfmetadata(
                "Book=Name", "Auth;or", markers);

            // global tags escaped
            Assert.Contains("title=Book\\=Name", text);
            Assert.Contains("artist=Auth\\;or", text);
            // chapter title escaped
            Assert.Contains("title=Title\\=with\\;special\\#chars\\\\and\\\nnewline", text);
        }
    }
}
