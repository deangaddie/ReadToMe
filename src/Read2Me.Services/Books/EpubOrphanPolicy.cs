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

            var orphanEntries = readingOrder
                .Select((f, spineIndex) => (f, spineIndex))
                .Where(x => !referencedPaths.Contains(x.f.FilePath) &&
                            contentByPath.TryGetValue(x.f.FilePath, out var ch) &&
                            ch.Paragraphs.Count > 0)
                .Select(x =>
                {
                    var ch = contentByPath[x.f.FilePath];
                    return (vol: new VolumeContent(ch.Title ?? string.Empty, [new PartContent(null, [ch])]),
                            x.spineIndex);
                })
                .ToList();

            if (orphanEntries.Count == 0) return navVolumes;

            var navEntries = navVolumes
                .Select((vol, i) => (vol, spineIndex: FirstReadingIndex(navPoints[i], readingOrder)));

            return navEntries
                .Concat(orphanEntries.Select(o => (o.vol, o.spineIndex)))
                .OrderBy(x => x.spineIndex)
                .Select(x => x.vol)
                .ToList();
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
