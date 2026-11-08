using System.Globalization;
using System.Text.RegularExpressions;

namespace Read2Me.Services.BookEdits
{
    /// <summary>What an <see cref="EditSource"/> reads, relative to the target it feeds.</summary>
    public enum EditSourceKind
    {
        /// <summary>The chapter's content paragraph by position from the start (p1…p5).</summary>
        Paragraph,
        /// <summary>…from the end, 1 = last (p_end1…p_end5).</summary>
        ParagraphFromEnd,
        /// <summary>The content paragraph before a paragraph target, within its chapter.</summary>
        Previous,
        /// <summary>The content paragraph after a paragraph target, within its chapter.</summary>
        Next,
        /// <summary>The enclosing chapter's title.</summary>
        Chapter,
        /// <summary>The enclosing part's title.</summary>
        Part,
        /// <summary>The enclosing volume's title.</summary>
        Volume,
        /// <summary>A child chapter's title by position (ch1…ch5); counted across the whole volume for a volume title.</summary>
        ChapterTitle,
        /// <summary>…from the end, 1 = last (ch_end1…ch_end5).</summary>
        ChapterTitleFromEnd,
        /// <summary>A volume's part title by position (pt1…pt5).</summary>
        PartTitle,
        /// <summary>…from the end, 1 = last (pt_end1…pt_end5).</summary>
        PartTitleFromEnd,
    }

    /// <summary>
    /// An Edit source: a value from a related node that a transform reads beside the target value it
    /// rewrites. <see cref="Position"/> is 1–5 for the positional kinds and 0 otherwise. Its
    /// <see cref="Name"/> is the one notation for template tokens, <c>transform.sources</c> and outline
    /// sample labels.
    /// </summary>
    public readonly record struct EditSource(EditSourceKind Kind, int Position = 0)
    {
        public string Name => Kind switch
        {
            EditSourceKind.Paragraph => $"p{Position}",
            EditSourceKind.ParagraphFromEnd => $"p_end{Position}",
            EditSourceKind.Previous => "prev",
            EditSourceKind.Next => "next",
            EditSourceKind.Chapter => "chapter",
            EditSourceKind.Part => "part",
            EditSourceKind.Volume => "volume",
            EditSourceKind.ChapterTitle => $"ch{Position}",
            EditSourceKind.ChapterTitleFromEnd => $"ch_end{Position}",
            EditSourceKind.PartTitle => $"pt{Position}",
            EditSourceKind.PartTitleFromEnd => $"pt_end{Position}",
            _ => throw new ArgumentOutOfRangeException(nameof(Kind), Kind, null),
        };

        public bool FromEnd => Kind is EditSourceKind.ParagraphFromEnd
            or EditSourceKind.ChapterTitleFromEnd or EditSourceKind.PartTitleFromEnd;

        /// <summary>The 0-based index a positional source reads in a list of <paramref name="count"/>;
        /// out of range when the position is past the end.</summary>
        public int IndexIn(int count) => FromEnd ? count - Position : Position - 1;

        public bool ReadsParagraphs => Kind is EditSourceKind.Paragraph or EditSourceKind.ParagraphFromEnd
            or EditSourceKind.Previous or EditSourceKind.Next;

        public bool ReadsChapterTitles => Kind is EditSourceKind.ChapterTitle or EditSourceKind.ChapterTitleFromEnd;

        public bool ReadsPartTitles => Kind is EditSourceKind.PartTitle or EditSourceKind.PartTitleFromEnd;

        public override string ToString() => Name;
    }

    /// <summary>
    /// One source as read for one target at plan time: its trimmed value, or why it is missing
    /// (past the end, a blank title, or the paragraph being edited). A row with a missing source fails
    /// with <see cref="MissingReason"/>.
    /// </summary>
    public sealed record EditSourceValue(string Name, string? Value, string? MissingReason)
    {
        public bool IsMissing => MissingReason != null;
    }

    /// <summary>
    /// The shared definition of every Edit source name: which targets may read it, its plain label,
    /// and the Unsupported reason for a pairing the catalogue disallows. The parser, the template
    /// renderer, the batch prompt's glosses, review rows and the plan summary all read it.
    /// </summary>
    public static class EditSourceCatalog
    {
        /// <summary>Positional sources reach 1–5 from the start or end.</summary>
        public const int MaxPosition = 5;

        private static readonly Regex PositionalName = new(@"^(p|p_end|ch|ch_end|pt|pt_end)(\d+)$", RegexOptions.CultureInvariant);

        /// <summary>Braces around a bare identifier; anything else in braces is literal text.</summary>
        internal static readonly Regex TokenPattern = new(@"\{([A-Za-z_][A-Za-z0-9_]*)\}", RegexOptions.CultureInvariant);

        public static IReadOnlyList<EditSource> All { get; } = BuildAll();

        /// <summary>Every source name, in catalogue order (the JSON schema's enum).</summary>
        public static IReadOnlyList<string> Names { get; } = All.Select(s => s.Name).ToArray();

        private static readonly Dictionary<string, EditSource> ByName = All.ToDictionary(s => s.Name, StringComparer.Ordinal);

        private static EditSource[] BuildAll()
        {
            static IEnumerable<EditSource> Run(EditSourceKind kind) =>
                Enumerable.Range(1, MaxPosition).Select(n => new EditSource(kind, n));
            return
            [
                .. Run(EditSourceKind.Paragraph), .. Run(EditSourceKind.ParagraphFromEnd),
                new(EditSourceKind.Previous), new(EditSourceKind.Next),
                new(EditSourceKind.Chapter), new(EditSourceKind.Part), new(EditSourceKind.Volume),
                .. Run(EditSourceKind.ChapterTitle), .. Run(EditSourceKind.ChapterTitleFromEnd),
                .. Run(EditSourceKind.PartTitle), .. Run(EditSourceKind.PartTitleFromEnd),
            ];
        }

        /// <summary>True for a catalogue name (positions 1–5 only).</summary>
        public static bool TryParse(string name, out EditSource source) => ByName.TryGetValue(name, out source);

        /// <summary>
        /// Null when <paramref name="target"/> may read <paramref name="name"/>; otherwise the plain
        /// Unsupported reason: a disallowed pairing, a position past 5, or
        /// <paramref name="unknownReason"/> for a name the catalogue doesn't have.
        /// </summary>
        public static string? Reject(string name, EditTargetSelector target, string unknownReason, out EditSource source)
        {
            if (!TryParse(name, out source))
                return PositionalName.IsMatch(name)
                    ? $"{name} is out of reach: sources count only 1 to {MaxPosition} from the start or end"
                    : unknownReason;
            return WhyUnreadable(target, source);
        }

        private static string? WhyUnreadable(EditTargetSelector target, EditSource source) => target switch
        {
            EditTargetSelector.VolumeTitle => source.Kind switch
            {
                _ when source.ReadsParagraphs => "Volume titles can't read paragraphs — rename the chapter first",
                EditSourceKind.Chapter => "Volume titles can't read chapter — use ch1…ch5 or ch_end1…ch_end5",
                EditSourceKind.Part => "Volume titles can't read part — use pt1…pt5 or pt_end1…pt_end5",
                EditSourceKind.Volume => "Volume titles can't read their own title — use {old}",
                _ => null,
            },
            EditTargetSelector.PartTitle => source.Kind switch
            {
                _ when source.ReadsParagraphs => "Part titles can't read paragraphs — rename the chapter first",
                EditSourceKind.Chapter => "Part titles can't read chapter — use ch1…ch5 or ch_end1…ch_end5",
                EditSourceKind.Part => "Part titles can't read their own title — use {old}",
                _ when source.ReadsPartTitles => "Part titles can't read other parts' titles",
                _ => null,
            },
            EditTargetSelector.ChapterTitle => source.Kind switch
            {
                EditSourceKind.Previous or EditSourceKind.Next =>
                    "Chapter titles can't read prev or next — use p1…p5 or p_end1…p_end5",
                EditSourceKind.Chapter => "Chapter titles can't read their own title — use {old}",
                _ when source.ReadsChapterTitles =>
                    "Chapter titles can't read other chapters' titles — an edit never reads across a chapter",
                _ when source.ReadsPartTitles => "Chapter titles can't read part titles by position — use part",
                _ => null,
            },
            _ => source switch
            {
                { ReadsChapterTitles: true } => "Paragraph edits can't read other chapters' titles — use chapter",
                { ReadsPartTitles: true } => "Paragraph edits can't read part titles by position — use part",
                _ => null,
            },
        };

        /// <summary>The plain gloss: "Paragraph 1", "Last paragraph", "Chapter title", "Part 1 title"….</summary>
        public static string Label(EditSource source) => source.Kind switch
        {
            EditSourceKind.Paragraph => $"Paragraph {N(source.Position)}",
            EditSourceKind.ParagraphFromEnd => $"{Last(source.Position)} paragraph",
            EditSourceKind.Previous => "Previous paragraph",
            EditSourceKind.Next => "Next paragraph",
            EditSourceKind.Chapter => "Chapter title",
            EditSourceKind.Part => "Part title",
            EditSourceKind.Volume => "Volume title",
            EditSourceKind.ChapterTitle => $"Chapter {N(source.Position)} title",
            EditSourceKind.ChapterTitleFromEnd => $"{Last(source.Position)} chapter title",
            EditSourceKind.PartTitle => $"Part {N(source.Position)} title",
            EditSourceKind.PartTitleFromEnd => $"{Last(source.Position)} part title",
            _ => throw new ArgumentOutOfRangeException(nameof(source), source.Kind, null),
        };

        /// <summary>The bare identifiers a template writes in braces, in first-use order, each once.
        /// Includes <c>n</c> and <c>old</c>.</summary>
        public static IReadOnlyList<string> TemplateTokens(string template) =>
            TokenPattern.Matches(template).Select(m => m.Groups[1].Value).Distinct(StringComparer.Ordinal).ToList();

        private static string N(int n) => n.ToString(CultureInfo.InvariantCulture);

        private static string Last(int position) => position switch
        {
            1 => "Last",
            2 => "Second-last",
            3 => "Third-last",
            4 => "Fourth-last",
            _ => "Fifth-last",
        };
    }
}
