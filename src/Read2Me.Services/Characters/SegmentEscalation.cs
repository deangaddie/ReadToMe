using Read2Me.Services.Llm;

namespace Read2Me.Services.Characters
{
    /// <summary>
    /// Escalation semantics for a segment-list attribution answer (escalation unit = whole
    /// paragraph). ParseFailure is not derived here — unparseable JSON, a missing batch index and
    /// fidelity/alignment failure are classified by the caller before segments exist.
    /// </summary>
    internal static class SegmentEscalation
    {
        private const string UnknownSentinel = "unknown";
        private const string NarratorSentinel = "narrator";

        /// <summary>
        /// Quality trigger for a parsed segment list: any dialog speaker outside the known list
        /// (and neither sentinel) → <see cref="EscalationTrigger.UnlistedName"/>; else any dialog
        /// speaker <c>"unknown"</c> → <see cref="EscalationTrigger.Unknown"/>; else
        /// <see cref="EscalationTrigger.None"/> (unlisted outranks unknown — final accept creates
        /// characters). An all-narration answer is None even for a dialog-queued paragraph: the
        /// re-segmentation overrides the earlier classifier.
        /// </summary>
        public static EscalationTrigger DeriveTrigger(
            IReadOnlyList<AttributionSegment> segments,
            IReadOnlyList<Data.Entities.Character> characters)
        {
            var anyUnknown = false;
            foreach (var segment in segments)
            {
                if (segment.Type != AttributionSegmentType.Dialog)
                    continue;

                var speaker = segment.Speaker.Trim();
                if (speaker.Equals(UnknownSentinel, StringComparison.OrdinalIgnoreCase))
                    anyUnknown = true;
                else if (!speaker.Equals(NarratorSentinel, StringComparison.OrdinalIgnoreCase) &&
                         !CharacterNames.IsKnown(speaker, characters))
                    return EscalationTrigger.UnlistedName;
            }

            return anyUnknown ? EscalationTrigger.Unknown : EscalationTrigger.None;
        }

        /// <summary>
        /// Self-consistency agreement: identical segment count, and per segment normalized text,
        /// type and canonicalized speaker (alias → owner name, OrdinalIgnoreCase) all match. Any
        /// difference is a disagreement; reasoning and voice instructions are ignored.
        /// </summary>
        public static bool AnswersAgree(
            SegmentAttributionResult a, SegmentAttributionResult b,
            IReadOnlyList<Data.Entities.Character> characters)
        {
            if (a.Segments.Count != b.Segments.Count)
                return false;

            for (var i = 0; i < a.Segments.Count; i++)
            {
                var sa = a.Segments[i];
                var sb = b.Segments[i];
                if (sa.Type != sb.Type)
                    return false;
                if (SegmentTextNormalizer.Normalize(sa.Text) != SegmentTextNormalizer.Normalize(sb.Text))
                    return false;
                if (!string.Equals(
                        CharacterNames.Canonicalize(sa.Speaker, characters),
                        CharacterNames.Canonicalize(sb.Speaker, characters),
                        StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            return true;
        }
    }
}
