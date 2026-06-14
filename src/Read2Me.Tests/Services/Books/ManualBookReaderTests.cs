using Read2Me.Core.Models;
using Read2Me.Services.Books;
using Xunit;

namespace Read2Me.Tests.Services.Books
{
    public class ManualBookReaderTests
    {
        // ── helpers ──────────────────────────────────────────────────────────

        private static BookContent Read(List<string> lines, ManualReadOptions options) =>
            ManualBookReader.Read(lines, options);

        private static ManualReadOptions ChaptersOnly(SplitDetectionMode mode, string? prefix = null) =>
            new(false, false, null, null, new SectionSplitRule(mode, prefix));

        private static ManualReadOptions WithParts(SectionSplitRule partRule, SectionSplitRule chapterRule) =>
            new(false, true, null, partRule, chapterRule);

        private static ManualReadOptions WithVolumes(SectionSplitRule volumeRule, SectionSplitRule chapterRule) =>
            new(true, false, volumeRule, null, chapterRule);

        private static ManualReadOptions Full(SectionSplitRule volumeRule, SectionSplitRule partRule, SectionSplitRule chapterRule) =>
            new(true, true, volumeRule, partRule, chapterRule);

        private static SectionSplitRule Prefix(string p) => new(SplitDetectionMode.Prefix, p);
        private static SectionSplitRule Number() => new(SplitDetectionMode.Number, null);
        private static SectionSplitRule Roman() => new(SplitDetectionMode.RomanNumeral, null);

        // ── no-match fallback ─────────────────────────────────────────────

        [Fact]
        public void NoMatchingPrefix_AllContentInSingleChapter()
        {
            var lines = new List<string> { "Para one", "Para two", "Para three" };
            var result = Read(lines, ChaptersOnly(SplitDetectionMode.Prefix, "Chapter"));

            Assert.Single(result.Volumes);
            Assert.Single(result.Volumes[0].Parts);
            var chapters = result.Volumes[0].Parts[0].Chapters;
            Assert.Single(chapters);
            Assert.Equal(3, chapters[0].Paragraphs.Count);
        }

        [Fact]
        public void EmptyLines_Dropped()
        {
            var lines = new List<string> { "Chapter 1", "", "  ", "Para one" };
            var result = Read(lines, ChaptersOnly(SplitDetectionMode.Prefix, "Chapter"));

            var paras = result.Volumes[0].Parts[0].Chapters[0].Paragraphs;
            Assert.Single(paras);
            Assert.Equal("Para one", paras[0].Text);
        }

        // ── chapter prefix splitting ──────────────────────────────────────

        [Fact]
        public void PrefixSplit_TwoChapters()
        {
            var lines = new List<string>
            {
                "Chapter 1",
                "First chapter content",
                "Chapter 2",
                "Second chapter content"
            };
            var result = Read(lines, ChaptersOnly(SplitDetectionMode.Prefix, "Chapter"));

            var chapters = result.Volumes[0].Parts[0].Chapters;
            Assert.Equal(2, chapters.Count);
            Assert.Equal("Chapter 1", chapters[0].Title);
            Assert.Equal("First chapter content", chapters[0].Paragraphs[0].Text);
            Assert.Equal("Chapter 2", chapters[1].Title);
            Assert.Equal("Second chapter content", chapters[1].Paragraphs[0].Text);
        }

        [Fact]
        public void PrefixSplit_CaseInsensitive()
        {
            var lines = new List<string> { "CHAPTER 1", "content", "chapter 2", "more" };
            var result = Read(lines, ChaptersOnly(SplitDetectionMode.Prefix, "chapter"));

            Assert.Equal(2, result.Volumes[0].Parts[0].Chapters.Count);
        }

        [Fact]
        public void PrefixSplit_BoundaryLineBecomesTitle_NotParagraph()
        {
            var lines = new List<string> { "Chapter 1", "Para" };
            var result = Read(lines, ChaptersOnly(SplitDetectionMode.Prefix, "Chapter"));

            var ch = result.Volumes[0].Parts[0].Chapters[0];
            Assert.Equal("Chapter 1", ch.Title);
            Assert.Single(ch.Paragraphs);
        }

        // ── number splitting ──────────────────────────────────────────────

        [Fact]
        public void NumberSplit_ThreeChapters()
        {
            var lines = new List<string>
            {
                "1", "First content",
                "2", "Second content",
                "3", "Third content"
            };
            var result = Read(lines, ChaptersOnly(SplitDetectionMode.Number));

            Assert.Equal(3, result.Volumes[0].Parts[0].Chapters.Count);
        }

        [Fact]
        public void NumberSplit_OnlyMatchesWholeLine()
        {
            // "1 Introduction" has trailing text — must NOT be a boundary
            var lines = new List<string> { "1 Introduction", "Para one" };
            var result = Read(lines, ChaptersOnly(SplitDetectionMode.Number));

            var chapters = result.Volumes[0].Parts[0].Chapters;
            Assert.Single(chapters);
            Assert.Equal(2, chapters[0].Paragraphs.Count);
        }

        [Fact]
        public void NumberSplit_WholeLineNumber_IsMatch()
        {
            var lines = new List<string> { "1", "Content", "2", "More" };
            var result = Read(lines, ChaptersOnly(SplitDetectionMode.Number));

            Assert.Equal(2, result.Volumes[0].Parts[0].Chapters.Count);
        }

        // ── roman numeral splitting ───────────────────────────────────────

        [Fact]
        public void RomanSplit_BasicChapters()
        {
            var lines = new List<string>
            {
                "I", "First content",
                "II", "Second content",
                "III", "Third content"
            };
            var result = Read(lines, ChaptersOnly(SplitDetectionMode.RomanNumeral));

            Assert.Equal(3, result.Volumes[0].Parts[0].Chapters.Count);
            Assert.Equal("I", result.Volumes[0].Parts[0].Chapters[0].Title);
        }

        [Fact]
        public void RomanSplit_CaseInsensitive()
        {
            var lines = new List<string> { "i", "content", "ii", "more" };
            var result = Read(lines, ChaptersOnly(SplitDetectionMode.RomanNumeral));

            Assert.Equal(2, result.Volumes[0].Parts[0].Chapters.Count);
        }

        [Fact]
        public void RomanSplit_OnlyMatchesWholeLine()
        {
            // "I. Introduction" has trailing text — must NOT be a boundary
            var lines = new List<string> { "I. Introduction", "content" };
            var result = Read(lines, ChaptersOnly(SplitDetectionMode.RomanNumeral));

            Assert.Single(result.Volumes[0].Parts[0].Chapters);
            Assert.Equal(2, result.Volumes[0].Parts[0].Chapters[0].Paragraphs.Count);
        }

        [Fact]
        public void RomanSplit_PlainWordNotMatched()
        {
            // "FIVE" is not a roman numeral (F is not valid) — both lines become paragraphs
            var lines = new List<string> { "FIVE", "content" };
            var result = Read(lines, ChaptersOnly(SplitDetectionMode.RomanNumeral));

            Assert.Single(result.Volumes[0].Parts[0].Chapters);
            Assert.Equal(2, result.Volumes[0].Parts[0].Chapters[0].Paragraphs.Count);
        }

        // ── parts splitting ───────────────────────────────────────────────

        [Fact]
        public void PartsSplit_TwoParts_EachWithChapters()
        {
            var lines = new List<string>
            {
                "Part 1",
                "Chapter 1", "Content A",
                "Chapter 2", "Content B",
                "Part 2",
                "Chapter 3", "Content C"
            };
            var result = Read(lines, WithParts(Prefix("Part"), Prefix("Chapter")));

            Assert.Single(result.Volumes);
            var parts = result.Volumes[0].Parts;
            Assert.Equal(2, parts.Count);
            Assert.Equal("Part 1", parts[0].Title);
            Assert.Equal(2, parts[0].Chapters.Count);
            Assert.Equal("Part 2", parts[1].Title);
            Assert.Single(parts[1].Chapters);
        }

        // ── volumes splitting ─────────────────────────────────────────────

        [Fact]
        public void VolumesSplit_TwoVolumes_EachWithChapters()
        {
            var lines = new List<string>
            {
                "Volume 1",
                "Chapter 1", "Content A",
                "Volume 2",
                "Chapter 2", "Content B"
            };
            var result = Read(lines, WithVolumes(Prefix("Volume"), Prefix("Chapter")));

            Assert.Equal(2, result.Volumes.Count);
            Assert.Equal("Volume 1", result.Volumes[0].Title);
            Assert.Single(result.Volumes[0].Parts[0].Chapters);
            Assert.Equal("Volume 2", result.Volumes[1].Title);
        }

        // ── full three-level split ────────────────────────────────────────

        [Fact]
        public void FullSplit_VolumesPartChapters()
        {
            var lines = new List<string>
            {
                "Volume 1",
                "Part 1",
                "Chapter 1", "Para A",
                "Chapter 2", "Para B",
                "Part 2",
                "Chapter 3", "Para C",
                "Volume 2",
                "Part 1",
                "Chapter 1", "Para D"
            };
            var result = Read(lines, Full(Prefix("Volume"), Prefix("Part"), Prefix("Chapter")));

            Assert.Equal(2, result.Volumes.Count);
            Assert.Equal(2, result.Volumes[0].Parts.Count);
            Assert.Equal(2, result.Volumes[0].Parts[0].Chapters.Count);
            Assert.Single(result.Volumes[0].Parts[1].Chapters);
            Assert.Single(result.Volumes[1].Parts);
        }

    }
}
