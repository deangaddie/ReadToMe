using System.Collections.Generic;
using System.Text.RegularExpressions;
using Read2Me.Core.Models;

namespace Read2Me.Services.Books
{
    public static partial class ManualBookReader
    {
        public static BookContent Read(List<string> lines, ManualReadOptions options)
        {
            var nonEmpty = new List<string>();
            foreach (var l in lines)
                if (!string.IsNullOrWhiteSpace(l))
                    nonEmpty.Add(l);

            return Split(nonEmpty, options);
        }

        private static BookContent Split(List<string> lines, ManualReadOptions options)
        {
            if (options.HasVolumes)
                return SplitByVolumes(lines, options);
            if (options.HasParts)
                return SplitByParts(lines, options, "Volume 1");
            return SplitByChapters(lines, options, "Volume 1", null);
        }

        private static BookContent SplitByVolumes(List<string> lines, ManualReadOptions options)
        {
            var volumes = new List<VolumeContent>();
            var currentTitle = "Volume 1";
            var currentLines = new List<string>();

            void FlushVolume()
            {
                var inner = options.HasParts
                    ? SplitByParts(currentLines, options, currentTitle)
                    : SplitByChapters(currentLines, options, currentTitle, null);

                // inner always has exactly one volume (the title is ignored here)
                if (inner.Volumes.Count > 0)
                    volumes.Add(new VolumeContent(currentTitle, inner.Volumes[0].Parts));
                else
                    volumes.Add(new VolumeContent(currentTitle, []));
            }

            foreach (var line in lines)
            {
                if (IsMatch(line, options.VolumeRule!))
                {
                    if (currentLines.Count > 0 || volumes.Count > 0)
                        FlushVolume();
                    currentTitle = line;
                    currentLines = [];
                }
                else
                {
                    currentLines.Add(line);
                }
            }
            FlushVolume();

            if (volumes.Count == 0)
                volumes.Add(new VolumeContent("Volume 1", [new PartContent(null, [new ChapterContent("Chapter 1", [])])]));

            return new BookContent(volumes);
        }

        private static BookContent SplitByParts(List<string> lines, ManualReadOptions options, string volumeTitle)
        {
            var parts = new List<PartContent>();
            string? currentTitle = null;
            var currentLines = new List<string>();

            void FlushPart()
            {
                var inner = SplitByChapters(currentLines, options, volumeTitle, currentTitle);
                var chapters = inner.Volumes.Count > 0 && inner.Volumes[0].Parts.Count > 0
                    ? inner.Volumes[0].Parts[0].Chapters
                    : new List<ChapterContent>();
                parts.Add(new PartContent(currentTitle, chapters));
            }

            foreach (var line in lines)
            {
                if (IsMatch(line, options.PartRule!))
                {
                    if (currentLines.Count > 0 || parts.Count > 0)
                        FlushPart();
                    currentTitle = line;
                    currentLines = [];
                }
                else
                {
                    currentLines.Add(line);
                }
            }
            FlushPart();

            if (parts.Count == 0)
                parts.Add(new PartContent(null, [new ChapterContent("Chapter 1", [])]));

            return new BookContent([new VolumeContent(volumeTitle, parts)]);
        }

        private static BookContent SplitByChapters(List<string> lines, ManualReadOptions options, string volumeTitle, string? partTitle)
        {
            var chapters = new List<ChapterContent>();
            string? currentTitle = null;
            var currentParas = new List<ParagraphContent>();

            void FlushChapter()
            {
                chapters.Add(new ChapterContent(currentTitle, currentParas));
            }

            foreach (var line in lines)
            {
                if (IsMatch(line, options.ChapterRule))
                {
                    if (currentParas.Count > 0 || chapters.Count > 0)
                        FlushChapter();
                    currentTitle = line;
                    currentParas = [];
                }
                else
                {
                    currentParas.Add(new ParagraphContent(line));
                }
            }
            FlushChapter();

            if (chapters.Count == 0)
                chapters.Add(new ChapterContent("Chapter 1", []));

            return new BookContent([new VolumeContent(volumeTitle, [new PartContent(partTitle, chapters)])]);
        }

        private static bool IsMatch(string line, SectionSplitRule rule)
        {
            return rule.Mode switch
            {
                SplitDetectionMode.Prefix => rule.Prefix is not null &&
                    line.StartsWith(rule.Prefix, System.StringComparison.OrdinalIgnoreCase),
                SplitDetectionMode.Number => NumberRegex().IsMatch(line.TrimStart()),
                SplitDetectionMode.RomanNumeral => !string.IsNullOrWhiteSpace(line) &&
                    RomanNumeralRegex().IsMatch(line.Trim()),
                _ => false
            };
        }

        [GeneratedRegex(@"^\d+$")]
        private static partial Regex NumberRegex();

        [GeneratedRegex(@"^(?:M{0,4})(?:CM|CD|D?C{0,3})(?:XC|XL|L?X{0,3})(?:IX|IV|V?I{0,3})$",
            RegexOptions.IgnoreCase)]
        private static partial Regex RomanNumeralRegex();
    }
}
