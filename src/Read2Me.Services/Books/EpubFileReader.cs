using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Read2Me.Core.Models;
using VersOne.Epub;

namespace Read2Me.Services.Books
{
    public partial class EpubFileReader(ILogger<EpubFileReader> logger)
    {
        public async Task<BookContent> ReadAsync(string filePath, CancellationToken cancellationToken = default)
        {
            logger.LogInformation("Reading EPUB: {Path}", filePath);

            var epub = await EpubReader.ReadBookAsync(filePath);

            var contentByPath = epub.ReadingOrder
                .ToDictionary(
                    f => f.FilePath,
                    f =>
                    {
                        var paras = ParseHtml(f.Content);
                        var title = ExtractHtmlTitle(f.Content) ?? f.FilePath;
                        return new ChapterContent(title, paras);
                    });

            var navPoints = epub.Navigation;

            if (navPoints is { Count: > 0 })
            {
                var navContent = TryBuildFromNav(navPoints, contentByPath, epub.Title ?? "Volume 1", epub.ReadingOrder);
                if (navContent is not null)
                {
                    logger.LogInformation("EPUB parsed: {Volumes} volume(s)", navContent.Volumes.Count);
                    return navContent;
                }
            }

            // Flat chapter list — wrap in a single Volume > Part
            var chapters = epub.ReadingOrder
                .Select(f => contentByPath.TryGetValue(f.FilePath, out var ch) ? ch
                    : new ChapterContent(f.FilePath, ParseHtml(f.Content)))
                .Where(ch => ch.Paragraphs.Count > 0)
                .ToList();

            logger.LogInformation("EPUB parsed (flat): {Chapters} chapter(s)", chapters.Count);
            return new BookContent([new VolumeContent("Volume 1", [new PartContent(null, chapters)])]);
        }

        internal static BookContent? TryBuildFromNav(
            IReadOnlyList<EpubNavigationItem> navPoints,
            Dictionary<string, ChapterContent> contentByPath,
            string bookTitle,
            IReadOnlyList<EpubLocalTextContentFile> readingOrder)
        {
            var topHasChildren = navPoints.Any(p => p.NestedItems is { Count: > 0 });
            if (!topHasChildren) return null;

            var hasNestedSections = navPoints.Any(p =>
                p.NestedItems is { Count: > 0 } &&
                p.NestedItems.Any(c => c.NestedItems is { Count: > 0 }));

            if (hasNestedSections)
            {
                // 3-level nav: top=volume, mid=part, bottom=chapter
                var navVolumes = navPoints.Select(p => BuildVolume(p, contentByPath, true)).ToList();
                navVolumes = EpubOrphanPolicy.Apply(navVolumes, navPoints, contentByPath, readingOrder);
                return new BookContent(navVolumes);
            }

            // 2-level nav: top=part, bottom=chapter — assign all parts to single volume
            var parts = navPoints.Select(p => BuildPart(p, contentByPath)).ToList();
            return new BookContent([new VolumeContent(bookTitle, parts)]);
        }

        private static VolumeContent BuildVolume(
            EpubNavigationItem navPoint,
            Dictionary<string, ChapterContent> contentByPath,
            bool hasNestedSections)
        {
            var title = navPoint.Title ?? string.Empty;
            var nested = navPoint.NestedItems;

            if (hasNestedSections && nested is { Count: > 0 })
            {
                var parts = nested.Select(c => BuildPart(c, contentByPath)).ToList();
                return new VolumeContent(title, parts);
            }

            // Top-level items are directly chapters — wrap in single Part
            var chapters = (nested is { Count: > 0 }
                ? nested.Select(c => ResolveChapter(c, contentByPath))
                : [ResolveChapter(navPoint, contentByPath)]).ToList();

            // Include section intro if nav point links to a different file than first child
            if (nested is { Count: > 0 })
            {
                var sectionLink = navPoint.Link?.ContentFilePath;
                var firstChildLink = nested[0].Link?.ContentFilePath;
                if (sectionLink is not null && sectionLink != firstChildLink &&
                    contentByPath.TryGetValue(sectionLink, out var intro) && intro.Paragraphs.Count > 0)
                    chapters.Insert(0, intro with { Title = title });
            }

            return new VolumeContent(title, [new PartContent(null, chapters)]);
        }

        private static PartContent BuildPart(
            EpubNavigationItem navPoint,
            Dictionary<string, ChapterContent> contentByPath)
        {
            var title = navPoint.Title;
            var nested = navPoint.NestedItems;

            List<ChapterContent> chapters;
            if (nested is { Count: > 0 })
            {
                chapters = nested.Select(c => ResolveChapter(c, contentByPath)).ToList();

                var sectionLink = navPoint.Link?.ContentFilePath;
                var firstChildLink = nested[0].Link?.ContentFilePath;
                if (sectionLink is not null && sectionLink != firstChildLink &&
                    contentByPath.TryGetValue(sectionLink, out var intro) && intro.Paragraphs.Count > 0)
                    chapters.Insert(0, intro with { Title = title ?? intro.Title });
            }
            else
            {
                chapters = [ResolveChapter(navPoint, contentByPath)];
            }

            return new PartContent(title, chapters);
        }

        private static ChapterContent ResolveChapter(
            EpubNavigationItem navPoint,
            Dictionary<string, ChapterContent> contentByPath)
        {
            var link = navPoint.Link?.ContentFilePath;
            if (link is not null && contentByPath.TryGetValue(link, out var ch))
                return ch with { Title = navPoint.Title ?? ch.Title };
            return new ChapterContent(navPoint.Title ?? string.Empty, []);
        }

        internal static List<ParagraphContent> ParseHtml(string html)
        {
            var paragraphs = new List<ParagraphContent>();

            html = HtmlHeadRegex().Replace(html, string.Empty);
            foreach (var filter in StripFilters())
                html = filter.Replace(html, string.Empty);

            var parts = BlockSplitRegex().Split(html);
            for (int i = 0; i < parts.Length; i++)
            {
                if (i > 0 && i % 2 == 1) continue; // skip captured tag strings

                var block = parts[i];
                var plain = StripTags(block).Trim();
                if (!string.IsNullOrWhiteSpace(plain))
                    paragraphs.Add(new ParagraphContent(plain));
            }

            return paragraphs;
        }

        internal static string? ExtractHtmlTitle(string html)
        {
            var m = HtmlTitleRegex().Match(html);
            if (!m.Success) return null;
            var text = StripTags(m.Groups[1].Value).Trim();
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }

        private static string StripTags(string html)
        {
            var s = BrTagRegex().Replace(html, " ");
            s = HtmlTagRegex().Replace(s, string.Empty);
            s = HtmlEntityRegex().Replace(s, m => m.Value switch
            {
                "&amp;" => "&",
                "&lt;" => "<",
                "&gt;" => ">",
                "&quot;" => "\"",
                "&apos;" => "'",
                "&nbsp;" => " ",
                "&mdash;" => "—",
                "&ndash;" => "–",
                "&ldquo;" => "“",
                "&rdquo;" => "”",
                "&lsquo;" => "‘",
                "&rsquo;" => "’",
                _ => m.Value,
            });
            return WhitespaceRegex().Replace(s, " ").Trim();
        }

        [GeneratedRegex(@"<br\s*/?>", RegexOptions.IgnoreCase)]
        private static partial Regex BrTagRegex();
        [GeneratedRegex(@"<[^>]+>")]
        private static partial Regex HtmlTagRegex();
        [GeneratedRegex(@"&[a-zA-Z]+;|&#[0-9]+;")]
        private static partial Regex HtmlEntityRegex();
        [GeneratedRegex(@"\s+")]
        private static partial Regex WhitespaceRegex();
        [GeneratedRegex(@"(<(?:p|div|h[1-6]|section|article|blockquote|hr)[^>]*>)", RegexOptions.IgnoreCase)]
        private static partial Regex BlockSplitRegex();
        [GeneratedRegex(@"<title[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
        private static partial Regex HtmlTitleRegex();
        [GeneratedRegex(@"<head[^>]*>.*?</head>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
        private static partial Regex HtmlHeadRegex();
        [GeneratedRegex(@"<div\s[^>]*\brole=(?:""figure""|'figure')[^>]*>.*?</div>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
        private static partial Regex FigureDivRegex();
        [GeneratedRegex(@"<span\s[^>]*\bclass=(?:""[^""]*\bcaption\b[^""]*""|'[^']*\bcaption\b[^']*')[^>]*>.*?</span>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
        private static partial Regex CaptionSpanRegex();

        private static IEnumerable<Regex> StripFilters() => [FigureDivRegex(), CaptionSpanRegex()];
    }
}
