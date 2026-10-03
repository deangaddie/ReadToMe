using System.Text;
using Read2Me.Data.Entities;

namespace Read2Me.Services.Characters.ChapterPass
{
    /// <summary>
    /// The chapter pass's prompt for one chapter, and where its cache contract lives.
    /// <list type="bullet">
    /// <item><see cref="SystemText"/> is built once: instructions, book, roster, rules, answer line.
    /// It never changes within the chapter.</item>
    /// <item><see cref="UserMessage"/> is <c>Passage:</c>, one line per snapshot paragraph from the
    /// chapter start to four past the current one, then the question. The lines are rendered from the
    /// snapshot only; the one thing that changes between calls is a <c>{Name} </c> label inserted by
    /// <see cref="SetLabel"/> on an item just answered. So every call's prefix up to the current
    /// paragraph is the previous call's, and llama reuses it from its prompt cache.</item>
    /// <item>A chapter too long for <see cref="ChapterPassBudget"/> starts at <see cref="TrimStart"/>
    /// instead of the chapter start; between trims the prefix property holds from there.</item>
    /// </list>
    /// <para>
    /// A paragraph's number <c>k</c> is its index in the snapshot, fixed when the snapshot was taken;
    /// an item's <c>ii</c> is its index in the paragraph's item list, which is what
    /// <c>AttributedItem.Index</c> names. Labels start from the stamps, except on the dialog of the
    /// paragraphs being asked: a stale stamp must not be shown as the answer to its own question.
    /// Partly attributed paragraphs are rendered as they are (unstamped items without a label), unlike
    /// the <c>Full</c> path's context.
    /// </para>
    /// </summary>
    internal sealed class ChapterPassPrompt
    {
        /// <summary>Look-ahead paragraphs after the current one: a speech tag often follows the quote it names.</summary>
        public const int After = 4;

        /// <summary>Passage lines <see cref="DisplayTail"/> shows.</summary>
        private const int TailLines = 5;

        private readonly IReadOnlyList<ChapterParagraph> _snapshot;
        private readonly string?[][] _labels;

        /// <param name="bookTitle">The book's title, as the prompt names it.</param>
        /// <param name="author">The book's author.</param>
        /// <param name="characters">The roster as read; only <see cref="RosterGrammar.Answerable"/> ones are offered.</param>
        /// <param name="snapshot">The chapter, read once (<c>GetChapterParagraphsForAttributionAsync</c>).</param>
        /// <param name="queued">The paragraphs this pass asks about; their dialog starts unlabelled.</param>
        public ChapterPassPrompt(
            string bookTitle, string author, IReadOnlyList<Character> characters,
            IReadOnlyList<ChapterParagraph> snapshot, IReadOnlySet<Guid> queued)
        {
            _snapshot = snapshot;
            var roster = RosterGrammar.Answerable(characters).ToList();
            Names = RosterGrammar.AnswerNames(roster.Select(c => c.Name));
            SystemText = BuildSystemText(bookTitle, author, roster);

            _labels = [.. snapshot.Select(p => p.Items
                .Select(i => i.IsDialog && !queued.Contains(p.ParagraphId) && IsStampedName(i.Speaker)
                    ? i.Speaker
                    : null)
                .ToArray())];
        }

        /// <summary>The names the model may answer with (the grammar's, minus <c>Unknown</c>).</summary>
        public IReadOnlyList<string> Names { get; }

        /// <summary>The system message, identical for every call of the chapter.</summary>
        public string SystemText { get; }

        /// <summary>Feeds an answer forward: item <paramref name="ii"/> of paragraph <paramref name="k"/> now shows <c>{name}</c>.</summary>
        public void SetLabel(int k, int ii, string name) => _labels[k][ii] = name;

        /// <summary>
        /// A rule tag shown before the first call, so the model sees it as an earlier answer: item
        /// <paramref name="ii"/> of paragraph <paramref name="k"/> shows <c>{name}</c> unless a stamp
        /// already labels it. The queued paragraphs start unlabelled, so their tags always show.
        /// </summary>
        public void SeedLabel(int k, int ii, string name) => _labels[k][ii] ??= name;

        /// <summary>The user message asking about item <paramref name="ii"/> of paragraph <paramref name="k"/>.</summary>
        public string UserMessage(int k, int ii) => Passage(k) + Question(k, ii);

        /// <summary>
        /// The first paragraph the passage shows (see <see cref="ChapterPassBudget"/>). It starts at 0 and only moves forward,
        /// by <see cref="Trim"/>; the lines keep their snapshot <c>k</c>.
        /// </summary>
        public int TrimStart { get; private set; }

        /// <summary>Whether asking about item <paramref name="ii"/> of paragraph <paramref name="k"/> would be over budget.</summary>
        public bool NeedsTrim(int k, int ii) =>
            ChapterPassBudget.NeedsTrim(SystemText.Length, Passage(k).Length, Question(k, ii).Length);

        /// <summary>
        /// Drops the oldest half of the lines before paragraph <paramref name="k"/> from the passage.
        /// False when there is nothing before it left to drop.
        /// </summary>
        public bool Trim(int k)
        {
            var next = ChapterPassBudget.NextTrimStart(TrimStart, k);
            if (next == TrimStart)
                return false;
            TrimStart = next;
            return true;
        }

        /// <summary><c>Passage:</c>, the lines from <see cref="TrimStart"/> to four past <paramref name="k"/>, and the blank line before the question.</summary>
        private string Passage(int k) =>
            "Passage:\n" + string.Join("\n", Lines(TrimStart, LastLine(k))) + "\n\n";

        /// <summary>
        /// What the live stream shows instead of the whole chapter: the passage's last
        /// <see cref="TailLines"/> lines and the question.
        /// </summary>
        public string DisplayTail(int k, int ii)
        {
            var last = LastLine(k);
            var first = Math.Max(TrimStart, last - TailLines + 1);
            return "Passage (tail):\n" + string.Join("\n", Lines(first, last)) + "\n\n" + Question(k, ii);
        }

        private int LastLine(int k) => Math.Min(k + After, _snapshot.Count - 1);

        private static string Question(int k, int ii) =>
            $"Who speaks ⟦{k}.{ii}⟧, and how is it delivered? Answer as Name | delivery.";

        private IEnumerable<string> Lines(int first, int last)
        {
            for (var k = first; k <= last; k++)
                yield return Line(k);
        }

        /// <summary><c>[k] </c> and the items joined by a space: narration as is, dialog marked and maybe labelled.</summary>
        private string Line(int k)
        {
            var items = _snapshot[k].Items;
            var sb = new StringBuilder().Append('[').Append(k).Append("] ");
            for (var ii = 0; ii < items.Count; ii++)
            {
                if (ii > 0)
                    sb.Append(' ');
                if (items[ii].IsDialog)
                {
                    if (_labels[k][ii] is { } name)
                        sb.Append('{').Append(name).Append("} ");
                    sb.Append('⟦').Append(k).Append('.').Append(ii).Append('⟧');
                }
                sb.Append(items[ii].Text);
            }
            return sb.ToString();
        }

        private static bool IsStampedName(string speaker) =>
            !Llm.AttributionWire.IsUnknownSpeaker(speaker) && !Llm.AttributionWire.IsNarrator(speaker);

        /// <summary>
        /// The instructions, verbatim from the prototype this pass was measured with, including the two
        /// rules that still say <c>"?"</c> for unknown: changing the wording changes the measured
        /// answers, so it waits for a measured run of its own.
        /// </summary>
        private static string BuildSystemText(string bookTitle, string author, IReadOnlyList<Character> roster)
        {
            var choices = string.Join("\n", roster.Select(c => c.Aliases.Count == 0
                ? $"- {c.Name}"
                : $"- {c.Name} (also: {string.Join(", ", c.Aliases.Select(a => a.Name))})"));
            if (choices.Length > 0)
                choices += "\n";

            return $"""
                You identify who speaks each line of dialogue in the novel "{bookTitle}" by {author}.

                Each dialogue item in the passage is marked ⟦paragraph.item⟧. Items already attributed show the speaker as {"{"}Name{"}"} before the marker.

                Choices (the whole book's cast, NOT a list of who is present):
                {choices}Unknown: not identified — the visible text has not yet named or uniquely described the speaker

                Rules:
                - Use attribution tags ("said X", "X replied") before or after the quote, actions by a named character in the same paragraph, who is addressed by name (usually NOT the speaker), and the alternation of a two-person conversation.
                - A listed character is a candidate only once the visible text has placed them in this scene. A speaker the text has only described ("a man", "the young officer") and not yet identified is "?", even if the book names them later.
                - Never pick a name by elimination or plausibility. A wrong name is worse than "?".
                - Answer as "Name | delivery": the name exactly as listed (or "Unknown"), then a short cue for the voice actor on how the line sounds: emotion, tone, volume or pace, in one or two adjectives plus at most one volume or pace word (e.g. "angry, shouting", "soft, hesitant", "dry, amused").
                  - If the narration says how the line is spoken ("softly", "whispered", "shouted"), the cue must agree with it.
                  - Describe only the voice. Never sounds or actions other than speech ("chuckling", "laughing", "sighing", "smiling"), gestures, or what the line is about. Never speech verbs ("said", "replied", "asked").
                  - Answer "plain" for a neutral or expository line.
                """.ReplaceLineEndings("\n");
        }
    }
}
