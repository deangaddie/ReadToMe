using Read2Me.Data;
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
        /// <summary>
        /// Quality trigger for a parsed segment list: any dialog speaker outside the known list
        /// (and neither sentinel, and not attested by this paragraph's own narration — see
        /// <see cref="IsAttestedInNarration"/>) → <see cref="EscalationTrigger.UnlistedName"/>; else
        /// any dialog speaker <c>"unknown"</c> → <see cref="EscalationTrigger.Unknown"/>; else
        /// <see cref="EscalationTrigger.None"/> (unlisted outranks unknown — final accept creates
        /// characters). An all-narration answer is None even for a dialog-queued paragraph: the
        /// re-segmentation overrides the earlier classifier.
        /// <para>
        /// The narrator token on a <em>dialog</em> segment is decided by the narrator link, not
        /// exempted. Linked it is a correct answer and burns no rung (<see cref="CharacterNames"/>
        /// canonicalizes it to the linked character). Unlinked it is
        /// <see cref="EscalationTrigger.Unknown"/> — a spoken line credited to someone by definition
        /// not in the scene, which stamped nobody. Never <see cref="EscalationTrigger.UnlistedName"/>:
        /// that tier means "a real name we lack", and its final accept would create a character out of
        /// a reserved token.
        /// </para>
        /// </summary>
        public static EscalationTrigger DeriveTrigger(
            IReadOnlyList<AttributionSegment> segments,
            IReadOnlyList<Data.Entities.Character> characters,
            NarratorIdentity narrator)
        {
            var anyUnknown = false;
            foreach (var segment in segments)
            {
                if (segment.Type != AttributionSegmentType.Dialog)
                    continue;

                var speaker = segment.Speaker.Trim();
                if (SegmentWire.IsUnknownSpeaker(speaker) || IsUnlinkedNarrator(speaker, narrator))
                    anyUnknown = true;
                else if (!CharacterNames.IsKnown(speaker, characters, narrator) &&
                         !IsAttestedInNarration(speaker, segments))
                    return EscalationTrigger.UnlistedName;
            }

            return anyUnknown ? EscalationTrigger.Unknown : EscalationTrigger.None;
        }

        /// <summary>The narrator token with no link behind it: nobody to stamp, so it means unknown.</summary>
        private static bool IsUnlinkedNarrator(string speaker, NarratorIdentity narrator) =>
            SegmentWire.IsNarrator(speaker) && !narrator.IsLinked;

        /// <summary>
        /// True when an unlisted speaker's name appears as a whole word in this paragraph's own
        /// narration — an attribution tag such as "…and Tathar said,". That is direct textual
        /// evidence, so the name is a first appearance rather than a hallucination and the answer
        /// need not escalate: the final accept will create the character.
        /// <para>
        /// Only narration counts. A name inside dialog is usually a vocative ("Well, John?"), where
        /// the named character is the one addressed, not the speaker — exactly the case escalation
        /// exists to catch.
        /// </para>
        /// </summary>
        private static bool IsAttestedInNarration(
            string speaker, IReadOnlyList<AttributionSegment> segments)
        {
            if (speaker.Length == 0)
                return false;

            foreach (var segment in segments)
            {
                if (segment.Type != AttributionSegmentType.Narration)
                    continue;
                if (ContainsWholeWord(segment.Text, speaker))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Case-insensitive whole-word containment: the match may not sit inside a longer word, so
        /// speaker "Tom" is not attested by the narration word "Tomas", while a possessive
        /// ("Tathar's") still attests.
        /// </summary>
        private static bool ContainsWholeWord(string haystack, string needle)
        {
            var from = 0;
            while (from <= haystack.Length - needle.Length)
            {
                var at = haystack.IndexOf(needle, from, StringComparison.OrdinalIgnoreCase);
                if (at < 0)
                    return false;

                var beforeOk = at == 0 || !char.IsLetterOrDigit(haystack[at - 1]);
                var end = at + needle.Length;
                var afterOk = end == haystack.Length || !char.IsLetterOrDigit(haystack[end]);
                if (beforeOk && afterOk)
                    return true;

                from = at + 1;
            }
            return false;
        }

        /// <summary>
        /// True when ≥1 dialog segment names nobody the apply can stamp — the wire sentinel
        /// <c>"unknown"</c>, or the narrator token with no link behind it — so the answer leaves part
        /// of the paragraph unattributed even if the rest of it stamps. Same two cases
        /// <see cref="DeriveTrigger"/> counts as unknown, so an answer's status and its trigger
        /// cannot contradict each other.
        /// </summary>
        public static bool HasUnknownSpeaker(
            IReadOnlyList<AttributionSegment> segments, NarratorIdentity narrator) =>
            segments.Any(s => s.Type == AttributionSegmentType.Dialog &&
                              (SegmentWire.IsUnknownSpeaker(s.Speaker) ||
                               IsUnlinkedNarrator(s.Speaker.Trim(), narrator)));

        /// <summary>
        /// True when the paragraph's existing split has dialog and the answer has none: every
        /// spoken line has been folded into narration.
        /// <para>
        /// This is the one wrong answer nothing else catches. The segment texts still reconstruct
        /// the paragraph, so alignment passes; <see cref="DeriveTrigger"/> and
        /// <see cref="HasUnknownSpeaker"/> both skip non-dialog segments, so the answer scores as a
        /// confident, fully-resolved one. Applying it deletes the Character item (the segment list
        /// is the whole truth about the paragraph), which both silences the line — it is read in
        /// the narrator's voice — and makes the paragraph invisible to the "any unattributed
        /// Character item" re-queue filter, so it can never be picked up again.
        /// </para>
        /// <para>
        /// It cannot collide with the other triggers: an answer with no dialog segments makes
        /// <see cref="DeriveTrigger"/> return <c>None</c> by construction, so this only ever
        /// promotes a <c>None</c>.
        /// </para>
        /// <para>
        /// The check is deliberately one-directional. The reverse — a prior split with no dialog and
        /// an answer that finds some — is the re-segmentation correcting the earlier classifier, and
        /// is exactly what it is for. This direction can also be a correct fix (the classifier can
        /// mis-mark a quoted phrase inside narration as speech), which is why the trigger escalates
        /// rather than rejects: a stronger rung that agrees still gets accepted.
        /// </para>
        /// </summary>
        /// <param name="prior">
        /// The paragraph's split before this answer, as loaded into the attribution context. Null
        /// when unavailable, which yields false — never guess a loss from missing evidence.
        /// </param>
        public static bool LosesDialog(
            IReadOnlyList<ContextSegment>? prior, IReadOnlyList<AttributionSegment> answer)
        {
            if (prior is null)
                return false;

            var priorHasDialog = prior.Any(
                s => string.Equals(s.Type, SegmentWire.Dialog, StringComparison.OrdinalIgnoreCase));
            if (!priorHasDialog)
                return false;

            return !answer.Any(s => s.Type == AttributionSegmentType.Dialog);
        }

        /// <summary>
        /// Self-consistency agreement: identical segment count, and per segment normalized text,
        /// type and canonicalized speaker (alias → owner name, OrdinalIgnoreCase) all match. Any
        /// difference is a disagreement; reasoning and voice instructions are ignored.
        /// <para>
        /// Canonicalization is narrator-aware, which is what this check needs most: self-consistency
        /// resamples at temperature, so under a link one sample can answer <c>narrator</c> and the
        /// other the linked character's name. Both are correct, and scoring them
        /// <see cref="EscalationTrigger.Inconsistent"/> would burn a stronger rung on two right
        /// answers — on exactly the books the narrator link exists for.
        /// </para>
        /// </summary>
        public static bool AnswersAgree(
            IReadOnlyList<AttributionSegment> a, IReadOnlyList<AttributionSegment> b,
            IReadOnlyList<Data.Entities.Character> characters,
            NarratorIdentity narrator)
        {
            if (a.Count != b.Count)
                return false;

            for (var i = 0; i < a.Count; i++)
            {
                var sa = a[i];
                var sb = b[i];
                if (sa.Type != sb.Type)
                    return false;
                if (SegmentTextNormalizer.Normalize(sa.Text) != SegmentTextNormalizer.Normalize(sb.Text))
                    return false;
                if (!string.Equals(
                        CharacterNames.Canonicalize(sa.Speaker, characters, narrator),
                        CharacterNames.Canonicalize(sb.Speaker, characters, narrator),
                        StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            return true;
        }
    }
}
