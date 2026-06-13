using Read2Me.Core.Models;
using VersOne.Epub;

namespace Read2Me.Services.Books
{
    internal static class EpubOrphanPolicy
    {
        internal static List<VolumeContent> Apply(
            List<VolumeContent> navVolumes,
            IReadOnlyList<EpubNavigationItem> navPoints,
            Dictionary<string, ChapterContent> contentByPath,
            IReadOnlyList<EpubLocalTextContentFile> readingOrder)
        {
            var referencedPaths = new HashSet<string>(CollectNavPaths(navPoints));

            var orphans = readingOrder
                .Select((f, i) => (file: f, index: i))
                .Where(x => !referencedPaths.Contains(x.file.FilePath) &&
                            contentByPath.TryGetValue(x.file.FilePath, out var ch) &&
                            ch.Paragraphs.Count > 0)
                .ToList();

            if (orphans.Count == 0) return navVolumes;

            var sectionFirstIndex = navPoints
                .Select((p, si) => (si, FirstReadingIndex(p, readingOrder)))
                .ToList();

            var result = new List<VolumeContent>(navVolumes);

            foreach (var (orphanFile, orphanIdx) in orphans.OrderByDescending(o => o.index))
            {
                var ch = contentByPath[orphanFile.FilePath];
                var orphanVol = new VolumeContent(ch.Title ?? string.Empty,
                    [new PartContent(null, [ch])]);

                var insertAt = sectionFirstIndex
                    .Where(s => s.Item2 > orphanIdx)
                    .Select(s => s.si)
                    .DefaultIfEmpty(result.Count)
                    .Min();

                result.Insert(insertAt, orphanVol);
                sectionFirstIndex = sectionFirstIndex
                    .Select(s => s.si >= insertAt ? (s.si + 1, s.Item2) : s)
                    .ToList();
            }

            return result;
        }

        internal static IEnumerable<string> CollectNavPaths(IEnumerable<EpubNavigationItem> items)
        {
            foreach (var item in items)
            {
                if (item.Link?.ContentFilePath is { } path) yield return path;
                if (item.NestedItems is { Count: > 0 })
                    foreach (var n in CollectNavPaths(item.NestedItems)) yield return n;
            }
        }

        private static int FirstReadingIndex(EpubNavigationItem nav, IReadOnlyList<EpubLocalTextContentFile> order)
        {
            var paths = CollectNavPaths([nav]).ToHashSet();
            for (int i = 0; i < order.Count; i++)
                if (paths.Contains(order[i].FilePath)) return i;
            return int.MaxValue;
        }
    }
}
