using System.Text;
using Read2Me.Services.Llm;

namespace Read2Me.Services.Characters.ChapterPass
{
    /// <summary>
    /// The chapter pass's answer contract (spec §4.4): a GBNF grammar that admits only a roster name
    /// or <c>Unknown</c>, then <c>" |"</c> and an optional short delivery cue, and the parse that maps
    /// such an answer back. The model can never name anyone off the list, so this path cannot create
    /// a Character.
    /// </summary>
    internal static class RosterGrammar
    {
        /// <summary>The answer for "not identified"; maps to <see cref="AttributionWire.Unknown"/>.</summary>
        public const string UnknownAnswer = "Unknown";

        /// <summary>The delivery cue: a space and up to 60 characters with no newline or bar (lab parity).</summary>
        private const string VoiceRule = "voice ::= ( \" \" [^\\n|]{1,60} )?";

        /// <summary>
        /// The names the model may answer with, in roster order: duplicates collapsed, and a roster
        /// name equal to <c>Unknown</c> (any case) dropped, since it would be indistinguishable from
        /// the sentinel (spec R9).
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
        /// Parses <c>Name | delivery</c>: split at the first <c>" |"</c>; the name must be one of
        /// <paramref name="names"/> exactly (→ itself) or <c>Unknown</c> (→
        /// <see cref="AttributionWire.Unknown"/>, delivery dropped — the escalation rung writes its
        /// own). The delivery is trimmed, and empty becomes null. Anything else is false.
        /// </summary>
        public static bool TryParse(
            string raw, IReadOnlyCollection<string> names, out string? name, out string? delivery)
        {
            var bar = raw.IndexOf(" |", StringComparison.Ordinal);
            var answered = (bar < 0 ? raw : raw[..bar]).Trim();

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
        /// null when empty or when there is no bar. A voice-only answer is read with this alone, since
        /// its name is the rule's, not the model's.
        /// </summary>
        public static string? ParseDelivery(string raw)
        {
            var bar = raw.IndexOf(" |", StringComparison.Ordinal);
            var cue = bar < 0 ? null : raw[(bar + 2)..].Trim();
            return string.IsNullOrEmpty(cue) ? null : cue;
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
