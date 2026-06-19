using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Read2Me.Core.Models;
using VersOne.Epub;

namespace Read2Me.Services.Books
{
    public sealed record EpubReadResult(BookContent Content, byte[]? CoverImage);

    public partial class EpubFileReader(ILogger<EpubFileReader> logger)
    {
        public async Task<EpubReadResult> ReadAsync(string filePath, CancellationToken cancellationToken = default)
        {
            logger.LogInformation("Reading EPUB: {Path}", filePath);

            var epub = await EpubReader.ReadBookAsync(filePath);

            var rawContentByPath = epub.ReadingOrder
                .ToDictionary(f => f.FilePath, f => f.Content);

            var contentByPath = rawContentByPath
                .ToDictionary(
                    kvp => kvp.Key,
                    kvp =>
                    {
                        var paras = ParseHtml(kvp.Value);
                        var title = ExtractHtmlTitle(kvp.Value) ?? kvp.Key;
                        return new ChapterContent(title, paras);
                    });

            var navPoints = epub.Navigation;
            BookContent content;

            if (navPoints is { Count: > 0 })
            {
                var anchoredContent = BuildAnchoredContent(navPoints, contentByPath, rawContentByPath);
                var navContent = TryBuildFromNav(navPoints, contentByPath, anchoredContent, epub.Title ?? "Volume 1", epub.ReadingOrder);
                if (navContent is not null)
                {
                    logger.LogInformation("EPUB parsed: {Volumes} volume(s)", navContent.Volumes.Count);
                    return new EpubReadResult(navContent, epub.CoverImage);
                }
            }

            // Flat chapter list — wrap in a single Volume > Part
            var chapters = epub.ReadingOrder
                .Select(f => contentByPath.TryGetValue(f.FilePath, out var ch) ? ch
                    : new ChapterContent(f.FilePath, ParseHtml(f.Content)))
                .Where(ch => ch.Paragraphs.Count > 0)
                .ToList();

            content = new BookContent([new VolumeContent("Volume 1", [new PartContent(null, chapters)])]);
            logger.LogInformation("EPUB parsed (flat): {Chapters} chapter(s)", chapters.Count);
            return new EpubReadResult(content, epub.CoverImage);
        }

        // Builds a lookup keyed by "filePath#anchor" for TOC entries that share a file.
        // For files with no anchored entries the full ChapterContent is kept under plain "filePath".
        internal static Dictionary<string, ChapterContent> BuildAnchoredContent(
            IReadOnlyList<EpubNavigationItem> navPoints,
            Dictionary<string, ChapterContent> contentByPath,
            Dictionary<string, string> rawContentByPath)
        {
            var result = new Dictionary<string, ChapterContent>(contentByPath, StringComparer.OrdinalIgnoreCase);

            var leaves = GetLeafNavItems(navPoints);

            // Group leaves by file; only process files that have at least one anchored entry
            var byFile = leaves
                .Where(n => n.Link?.ContentFilePath is not null)
                .GroupBy(n => n.Link!.ContentFilePath)
                .Where(g => g.Any(n => !string.IsNullOrEmpty(n.Link!.Anchor)));

            foreach (var group in byFile)
            {
                var path = group.Key;
                if (!rawContentByPath.TryGetValue(path, out var html)) continue;

                var fullParas = contentByPath.TryGetValue(path, out var full)
                    ? full.Paragraphs
                    : ParseHtml(html);

                var anchorMap = BuildAnchorMap(html);

                // Collect ordered (anchor, startPara) for this file's TOC entries
                var entries = group
                    .Select(n => (
                        anchor: n.Link!.Anchor ?? string.Empty,
                        title: n.Title ?? string.Empty,
                        start: string.IsNullOrEmpty(n.Link.Anchor) ? 0
                            : anchorMap.TryGetValue(n.Link.Anchor, out var idx) ? idx : -1
                    ))
                    .OrderBy(e => e.start)
                    .ToList();

                for (int i = 0; i < entries.Count; i++)
                {
                    var (anchor, title, start) = entries[i];
                    if (start < 0) continue; // anchor not found — skip; full file still available

                    var end = i + 1 < entries.Count && entries[i + 1].start >= 0
                        ? entries[i + 1].start
                        : fullParas.Count;

                    var slice = fullParas.Skip(start).Take(end - start).ToList();
                    var key = string.IsNullOrEmpty(anchor) ? path : $"{path}#{anchor}";
                    result[key] = new ChapterContent(title, slice);
                }
            }

            return result;
        }

        // Builds a map of anchor-id → paragraph index by scanning raw HTML in parallel with ParseHtml's block-split logic.
        internal static Dictionary<string, int> BuildAnchorMap(string html)
        {
            var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            html = HtmlHeadRegex().Replace(html, string.Empty);
            foreach (var filter in StripFilters())
                html = filter.Replace(html, string.Empty);

            var parts = BlockSplitRegex().Split(html);
            int paraIndex = 0;
            bool pendingIncrement = false;

            for (int i = 0; i < parts.Length; i++)
            {
                if (i % 2 == 1)
                {
                    // This is a captured block tag — record anchors within it
                    RecordAnchors(parts[i], paraIndex, map);
                    pendingIncrement = true;
                    continue;
                }

                // Text segment — record anchors here too (e.g. <a id="..."> inline)
                RecordAnchors(parts[i], paraIndex, map);

                var plain = StripTags(parts[i]).Trim();
                if (!string.IsNullOrWhiteSpace(plain))
                {
                    if (pendingIncrement) paraIndex++;
                    pendingIncrement = false;
                }
            }

            return map;
        }

        private static void RecordAnchors(string fragment, int paraIndex, Dictionary<string, int> map)
        {
            foreach (Match m in AnchorIdRegex().Matches(fragment))
            {
                var id = m.Groups[1].Value;
                if (!string.IsNullOrEmpty(id) && !map.ContainsKey(id))
                    map[id] = paraIndex;
            }
        }

        internal static List<EpubNavigationItem> GetLeafNavItems(IReadOnlyList<EpubNavigationItem> navPoints)
        {
            var leaves = new List<EpubNavigationItem>();
            foreach (var item in navPoints)
                CollectLeaves(item, leaves);
            return leaves;
        }

        private static void CollectLeaves(EpubNavigationItem item, List<EpubNavigationItem> leaves)
        {
            if (item.NestedItems is { Count: > 0 })
            {
                foreach (var child in item.NestedItems)
                    CollectLeaves(child, leaves);
            }
            else if (item.Link is not null)
            {
                leaves.Add(item);
            }
        }

        internal static BookContent? TryBuildFromNav(
            IReadOnlyList<EpubNavigationItem> navPoints,
            Dictionary<string, ChapterContent> contentByPath,
            Dictionary<string, ChapterContent> anchoredContent,
            string bookTitle,
            IReadOnlyList<EpubLocalTextContentFile> readingOrder)
        {
            var topHasChildren = navPoints.Any(p => p.NestedItems is { Count: > 0 });

            if (!topHasChildren)
            {
                // Flat nav — use anchored slices if any entry has an anchor, otherwise fall through
                var flatLeaves = navPoints.Where(p => p.Link is not null).ToList();
                var hasAnchors = flatLeaves.Any(p => !string.IsNullOrEmpty(p.Link!.Anchor));
                if (!hasAnchors) return null;

                var chapters = flatLeaves
                    .Select(p => ResolveChapter(p, anchoredContent))
                    .Where(ch => ch.Paragraphs.Count > 0)
                    .ToList();
                return new BookContent([new VolumeContent(bookTitle, [new PartContent(null, chapters)])]);
            }

            var hasNestedSections = navPoints.Any(p =>
                p.NestedItems is { Count: > 0 } &&
                p.NestedItems.Any(c => c.NestedItems is { Count: > 0 }));

            if (hasNestedSections)
            {
                // 3-level nav: top=volume, mid=part, bottom=chapter
                var navVolumes = navPoints.Select(p => BuildVolume(p, contentByPath, anchoredContent, true)).ToList();
                navVolumes = EpubOrphanPolicy.Apply(navVolumes, navPoints, contentByPath, readingOrder);
                return new BookContent(navVolumes);
            }

            // 2-level nav: top=part, bottom=chapter — assign all parts to single volume
            var parts = navPoints.Select(p => BuildPart(p, contentByPath, anchoredContent)).ToList();
            return new BookContent([new VolumeContent(bookTitle, parts)]);
        }

        private static VolumeContent BuildVolume(
            EpubNavigationItem navPoint,
            Dictionary<string, ChapterContent> contentByPath,
            Dictionary<string, ChapterContent> anchoredContent,
            bool hasNestedSections)
        {
            var title = navPoint.Title ?? string.Empty;
            var nested = navPoint.NestedItems;

            if (hasNestedSections && nested is { Count: > 0 })
            {
                var parts = nested.Select(c => BuildPart(c, contentByPath, anchoredContent)).ToList();
                return new VolumeContent(title, parts);
            }

            // Top-level items are directly chapters — wrap in single Part
            var chapters = (nested is { Count: > 0 }
                ? nested.Select(c => ResolveChapter(c, anchoredContent))
                : [ResolveChapter(navPoint, anchoredContent)]).ToList();

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
            Dictionary<string, ChapterContent> contentByPath,
            Dictionary<string, ChapterContent> anchoredContent)
        {
            var title = navPoint.Title;
            var nested = navPoint.NestedItems;

            List<ChapterContent> chapters;
            if (nested is { Count: > 0 })
            {
                chapters = nested.Select(c => ResolveChapter(c, anchoredContent)).ToList();

                var sectionLink = navPoint.Link?.ContentFilePath;
                var firstChildLink = nested[0].Link?.ContentFilePath;
                if (sectionLink is not null && sectionLink != firstChildLink &&
                    contentByPath.TryGetValue(sectionLink, out var intro) && intro.Paragraphs.Count > 0)
                    chapters.Insert(0, intro with { Title = title ?? intro.Title });
            }
            else
            {
                chapters = [ResolveChapter(navPoint, anchoredContent)];
            }

            return new PartContent(title, chapters);
        }

        private static ChapterContent ResolveChapter(
            EpubNavigationItem navPoint,
            Dictionary<string, ChapterContent> anchoredContent)
        {
            var link = navPoint.Link;
            if (link is null) return new ChapterContent(navPoint.Title ?? string.Empty, []);

            var path = link.ContentFilePath;
            var anchor = link.Anchor;

            // Prefer anchored slice; fall back to full file
            var key = !string.IsNullOrEmpty(anchor) ? $"{path}#{anchor}" : path;
            if (anchoredContent.TryGetValue(key, out var ch))
                return ch with { Title = navPoint.Title ?? ch.Title };

            if (anchoredContent.TryGetValue(path, out var fallback))
                return fallback with { Title = navPoint.Title ?? fallback.Title };

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
        [GeneratedRegex(@"(?:id|name)=""([^""]+)""", RegexOptions.IgnoreCase)]
        private static partial Regex AnchorIdRegex();

        private static IEnumerable<Regex> StripFilters() => [FigureDivRegex(), CaptionSpanRegex()];
    }
}
