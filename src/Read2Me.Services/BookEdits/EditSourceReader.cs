using System.Globalization;

namespace Read2Me.Services.BookEdits
{
    /// <summary>
    /// What one target can read its Edit sources from. Lists the target can't read stay null (the
    /// parser has already rejected those sources). <see cref="Paragraphs"/> holds the chapter's content
    /// paragraph values (pauses skipped, text items joined); <see cref="ParagraphIndex"/> is the
    /// target's own paragraph in it, or -1 for a title target. <see cref="ChapterTitlesOwner"/> names
    /// what <see cref="ChapterTitles"/> are counted across ("part" or "volume").
    /// </summary>
    internal sealed record EditSourceScope(
        string? VolumeTitle = null,
        string? PartTitle = null,
        string? ChapterTitle = null,
        IReadOnlyList<string>? Paragraphs = null,
        int ParagraphIndex = -1,
        IReadOnlyList<string?>? ChapterTitles = null,
        string ChapterTitlesOwner = "part",
        IReadOnlyList<string?>? PartTitles = null);

    /// <summary>
    /// Reads a plan's named sources for one target: each a trimmed value, or a missing reason naming
    /// the source and why (past the end, a blank title, the paragraph being edited). Nothing here reads
    /// across a chapter: the scope only holds the target's own chapter's paragraphs.
    /// </summary>
    internal static class EditSourceReader
    {
        public static IReadOnlyList<EditSourceValue> Read(IReadOnlyList<EditSource> sources, EditSourceScope scope) =>
            sources.Select(s => Read(s, scope)).ToList();

        private static EditSourceValue Read(EditSource source, EditSourceScope scope)
        {
            var name = source.Name;
            switch (source.Kind)
            {
                case EditSourceKind.Paragraph or EditSourceKind.ParagraphFromEnd:
                {
                    var paragraphs = scope.Paragraphs ?? [];
                    if (source.Position > paragraphs.Count)
                        return Missing(name, paragraphs.Count == 0
                            ? "the chapter has no paragraphs"
                            : $"the chapter has only {Count(paragraphs.Count, "paragraph")}");
                    var index = source.IndexIn(paragraphs.Count);
                    return index == scope.ParagraphIndex
                        ? new EditSourceValue(name, null, $"{name} is the paragraph being edited")
                        : Found(name, paragraphs[index]);
                }
                case EditSourceKind.Previous or EditSourceKind.Next:
                {
                    var paragraphs = scope.Paragraphs ?? [];
                    var index = scope.ParagraphIndex + (source.Kind == EditSourceKind.Previous ? -1 : 1);
                    if (index < 0)
                        return Missing(name, "this is the chapter's first paragraph");
                    if (index >= paragraphs.Count)
                        return Missing(name, "this is the chapter's last paragraph");
                    return Found(name, paragraphs[index]);
                }
                case EditSourceKind.Chapter:
                    return Title(name, scope.ChapterTitle, "the chapter title is blank");
                case EditSourceKind.Part:
                    return Title(name, scope.PartTitle, "the part title is blank");
                case EditSourceKind.Volume:
                    return Title(name, scope.VolumeTitle, "the volume title is blank");
                case EditSourceKind.ChapterTitle or EditSourceKind.ChapterTitleFromEnd:
                    return ByPosition(source, scope.ChapterTitles ?? [], scope.ChapterTitlesOwner, "chapter");
                case EditSourceKind.PartTitle or EditSourceKind.PartTitleFromEnd:
                    return ByPosition(source, scope.PartTitles ?? [], "volume", "part");
                default:
                    throw new ArgumentOutOfRangeException(nameof(source), source.Kind, null);
            }
        }

        private static EditSourceValue ByPosition(EditSource source, IReadOnlyList<string?> titles, string owner, string level)
        {
            if (source.Position > titles.Count)
                return Missing(source.Name, titles.Count == 0
                    ? $"the {owner} has no {level}s"
                    : $"the {owner} has only {Count(titles.Count, level)}");
            var index = source.IndexIn(titles.Count);
            return Title(source.Name, titles[index], $"that {level} title is blank");
        }

        private static EditSourceValue Title(string name, string? title, string blank) =>
            string.IsNullOrWhiteSpace(title) ? Missing(name, blank) : Found(name, title);

        private static EditSourceValue Found(string name, string value) => new(name, value.Trim(), null);

        private static EditSourceValue Missing(string name, string why) => new(name, null, $"{name} is missing: {why}");

        private static string Count(int n, string noun) =>
            $"{n.ToString(CultureInfo.InvariantCulture)} {noun}{(n == 1 ? "" : "s")}";
    }
}
