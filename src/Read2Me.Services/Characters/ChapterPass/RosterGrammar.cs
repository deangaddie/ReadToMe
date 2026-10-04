using System.Text;
using Read2Me.Data;
using Read2Me.Data.Entities;
using Read2Me.Services.Llm;

namespace Read2Me.Services.Characters.ChapterPass
{
    /// <summary>
    /// The chapter pass's answer contract: a GBNF grammar that admits only a roster name or
    /// <c>Unknown</c>, then <c>" |"</c> and an optional short delivery cue, and the parse that maps
    /// such an answer back. The model can never name anyone off the list, so its answer cannot
    /// create a Character; only rules-discover adds to the roster, before the chapter is asked.
    /// </summary>
    internal static class RosterGrammar
    {
        /// <summary>The answer for "not identified"; maps to <see cref="AttributionWire.Unknown"/>.</summary>
        public const string UnknownAnswer = "Unknown";

        /// <summary>The cue the prompt asks for on a neutral or expository line; maps to no delivery.</summary>
        public const string PlainAnswer = "plain";

        /// <summary>
        /// The delivery cue: a space and up to 40 characters with no newline or bar. Longer cues
        /// drift from how the line sounds into what it is about.
        /// </summary>
        private const string VoiceRule = "voice ::= ( \" \" [^\\n|]{1,40} )?";

        /// <summary>Between the name and the cue.</summary>
        private const string Bar = " |";

        /// <summary>
        /// The characters the pass can answer with: all but the seed Narrator row (narration is not
        /// asked about) and any whose name collides with <c>Unknown</c>.
        /// </summary>
        public static IEnumerable<Character> Answerable(IEnumerable<Character> characters) =>
            characters.Where(c => c.Id != ProjectDbContext.NarratorId && !IsReserved(c.Name));

        /// <summary>
        /// The names the model may answer with, in roster order: duplicates collapsed, and a roster
        /// name equal to <c>Unknown</c> (any case) dropped, since an answer of it could not be told
        /// from the sentinel.
        /// </summary>
        public static IReadOnlyList<string> AnswerNames(IEnumerable<string> roster) =>
            [.. roster.Where(n => !IsReserved(n)).Distinct(StringComparer.Ordinal)];

        /// <summary>True for a roster name the grammar cannot carry: it collides with the sentinel.</summary>
        public static bool IsReserved(string name) =>
            name.Trim().Equals(UnknownAnswer, StringComparison.OrdinalIgnoreCase);

        /// <summary><c>root ::= name " |" voice</c> over the roster names plus <c>Unknown</c>.</summary>
        public static string ForRoster(IEnumerable<string> roster)
        {
            var alternatives = AnswerNames(roster).Append(UnknownAnswer).Select(Literal);
            return "root ::= name \" |\" voice\n"
                + $"name ::= {string.Join(" | ", alternatives)}\n"
                + VoiceRule;
        }

        /// <summary>The voice-only grammar: the name is fixed, only the delivery is free.</summary>
        public static string ForName(string name) =>
            $"root ::= {Literal(name)} \" |\" voice\n" + VoiceRule;

        /// <summary>
        /// Parses <c>Name | delivery</c>: the name must be one of <paramref name="names"/> exactly
        /// (→ itself) or <c>Unknown</c> (→ <see cref="AttributionWire.Unknown"/>, delivery dropped —
        /// the escalation rung writes its own). The delivery is read as <see cref="ParseDelivery"/>
        /// reads it. Anything else is false.
        /// </summary>
        public static bool TryParse(
            string raw, IReadOnlyCollection<string> names, out string? name, out string? delivery)
        {
            var answered = Split(raw).Name.Trim();

            if (answered == UnknownAnswer)
            {
                name = AttributionWire.Unknown;
                delivery = null;
                return true;
            }

            if (answered.Length > 0 && names.Contains(answered, StringComparer.Ordinal))
            {
                name = answered;
                delivery = ParseDelivery(raw);
                return true;
            }

            name = null;
            delivery = null;
            return false;
        }

        /// <summary>
        /// The delivery half of <c>Name | delivery</c>: what follows the first <c>" |"</c>, trimmed;
        /// null when empty, when there is no bar, or when it is <see cref="PlainAnswer"/> (any case):
        /// a neutral line gets no instruction. A voice-only answer is read with this alone, since its
        /// name is the rule's, not the model's.
        /// </summary>
        public static string? ParseDelivery(string raw)
        {
            var cue = Split(raw).Cue?.Trim();
            return string.IsNullOrEmpty(cue) || cue.Equals(PlainAnswer, StringComparison.OrdinalIgnoreCase)
                ? null
                : cue;
        }

        /// <summary>The answer split at its first <c>" |"</c>; no cue when there is no bar.</summary>
        private static (string Name, string? Cue) Split(string raw)
        {
            var bar = raw.IndexOf(Bar, StringComparison.Ordinal);
            return bar < 0 ? (raw, null) : (raw[..bar], raw[(bar + Bar.Length)..]);
        }

        /// <summary>A GBNF string literal: backslash, quote and control characters escaped; UTF-8 as is.</summary>
        private static string Literal(string value)
        {
            var sb = new StringBuilder(value.Length + 2).Append('"');
            foreach (var c in value)
            {
                switch (c)
                {
                    case '\\': sb.Append(@"\\"); break;
                    case '"': sb.Append(@"\"""); break;
                    case '\n': sb.Append(@"\n"); break;
                    case '\r': sb.Append(@"\r"); break;
                    case '\t': sb.Append(@"\t"); break;
                    default: sb.Append(c); break;
                }
            }
            return sb.Append('"').ToString();
        }
    }
}
