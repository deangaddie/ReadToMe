using Read2Me.Data;
using Read2Me.Services.Llm;

namespace Read2Me.Services.Characters
{
    /// <summary>
    /// Escalation semantics for a per-item attribution answer (escalation unit = whole paragraph).
    /// The answer names existing items by index — boundaries are frozen (ADR 0005) — so every check
    /// here reads the answer <em>against</em> the paragraph's own items, which is what makes
    /// "the model said nothing about this dialog item" a first-class case.
    /// ParseFailure is not derived here: unparseable JSON and a missing batch index are classified by
    /// the caller before an answer exists.
    /// <para>
    /// There is deliberately no "the answer lost the paragraph's dialog" check. It existed because an
    /// answer used to replace the paragraph's items and could delete one; per ADR 0005 no answer can
    /// remove an item, so the defect it caught is unreachable. Do not re-add it.
    /// </para>
    /// </summary>
    internal static class ItemAttributionEscalation
    {
        /// <summary>
        /// Quality trigger for a parsed answer, judged over the paragraph's own dialog items: any
        /// answered speaker outside the known list (and neither sentinel, and not attested by this
        /// paragraph's own narration — see <see cref="IsAttestedInNarration"/>) →
        /// <see cref="EscalationTrigger.UnlistedName"/>; else any dialog item the answer leaves
        /// unstamped → <see cref="EscalationTrigger.Unknown"/>; else
        /// <see cref="EscalationTrigger.None"/> (unlisted outranks unknown — final accept creates
        /// characters). Answers on narration indices are ignored, never rejected.
        /// <para>
        /// A dialog item the answer never mentions is <see cref="EscalationTrigger.Unknown"/>, the
        /// same as one answered <c>"unknown"</c>: both mean the rung failed to name its speaker, and
        /// the item stays unattributed either way.
        /// </para>
        /// <para>
        /// The narrator token on a <em>dialog</em> item is decided by the narrator link, not
        /// exempted. Linked it is a correct answer and burns no rung (<see cref="CharacterNames"/>
        /// canonicalizes it to the linked character). Unlinked it is
        /// <see cref="EscalationTrigger.Unknown"/> — a spoken line credited to someone by definition
        /// not in the scene, which stamped nobody. Never <see cref="EscalationTrigger.UnlistedName"/>:
        /// that tier means "a real name we lack", and its final accept would create a character out of
        /// a reserved token.
        /// </para>
        /// </summary>
        /// <param name="answer">The items the model named, by prompt index.</param>
        /// <param name="items">
        /// The paragraph's items as the prompt numbered them (index = position, <c>Order</c>
        /// sequence). Both the set of answerable dialog indices and the narration text attestation
        /// reads come from here.
        /// </param>
        public static EscalationTrigger DeriveTrigger(
            IReadOnlyList<AttributedItem> answer,
            IReadOnlyList<ContextItem> items,
            IReadOnlyList<Data.Entities.Character> characters,
            NarratorIdentity narrator)
        {
            var speakers = SpeakersByIndex(answer);
            var anyUnknown = false;

            for (var index = 0; index < items.Count; index++)
            {
                if (!items[index].IsDialog)
                    continue;

                if (LeavesUnstamped(index, speakers, narrator))
                {
                    anyUnknown = true;
                    continue;
                }

                var speaker = speakers[index];
                if (!CharacterNames.IsKnown(speaker, characters, narrator) &&
                    !IsAttestedInNarration(speaker, items))
                    return EscalationTrigger.UnlistedName;
            }

            return anyUnknown ? EscalationTrigger.Unknown : EscalationTrigger.None;
        }

        /// <summary>
        /// True when the answer leaves ≥1 dialog item unstamped — unanswered, the wire sentinel
        /// <c>"unknown"</c>, or the narrator token with no link behind it — so part of the paragraph
        /// stays unattributed even if the rest of it stamps. Exactly the cases
        /// <see cref="DeriveTrigger"/> counts as unknown, so an answer's status and its trigger
        /// cannot contradict each other.
        /// </summary>
        public static bool HasUnknownSpeaker(
            IReadOnlyList<AttributedItem> answer,
            IReadOnlyList<ContextItem> items,
            NarratorIdentity narrator)
        {
            var speakers = SpeakersByIndex(answer);
            for (var index = 0; index < items.Count; index++)
                if (items[index].IsDialog && LeavesUnstamped(index, speakers, narrator))
                    return true;
            return false;
        }

        /// <summary>
        /// Self-consistency agreement: the two samples must name the same set of <em>stampable</em>
        /// indices, and each index's speaker must canonicalize equal (alias → owner name,
        /// OrdinalIgnoreCase). Coverage counts as much as content — an index one sample answered and
        /// the other did not is a disagreement, because the two rungs disagree about whether that
        /// item has a speaker at all. Voice instructions and reasoning are ignored.
        /// <para>
        /// Answers on narration indices are ignored on both sides, as everywhere else (spec §2): one
        /// sample volunteering a speaker for narration the other left alone is not a disagreement
        /// about anything that can be applied, and scoring it one would burn a stronger rung over an
        /// answer nobody reads. The stampable set comes off the answer's own index→id map, which is
        /// the same map for both samples — they answered one ask about one paragraph.
        /// </para>
        /// <para>
        /// Canonicalization is narrator-aware, which is what this check needs most: self-consistency
        /// resamples at temperature, so under a link one sample can answer <c>narrator</c> and the
        /// other the linked character's name. Both are correct, and scoring them
        /// <see cref="EscalationTrigger.Inconsistent"/> would burn a stronger rung on two right
        /// answers — on exactly the books the narrator link exists for.
        /// </para>
        /// </summary>
        public static bool AnswersAgree(
            AttributionAnswer a, AttributionAnswer b,
            IReadOnlyList<Data.Entities.Character> characters,
            NarratorIdentity narrator)
        {
            var left = StampableSpeakersByIndex(a);
            var right = StampableSpeakersByIndex(b.Items, a.ItemIds);
            if (left.Count != right.Count)
                return false;

            foreach (var (index, speaker) in left)
            {
                if (!right.TryGetValue(index, out var other))
                    return false;
                if (!string.Equals(
                        CharacterNames.Canonicalize(speaker, characters, narrator),
                        CharacterNames.Canonicalize(other, characters, narrator),
                        StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            return true;
        }

        /// <summary>
        /// The answer as index→speaker, keeping only indices that name a stampable item — the
        /// comparable part of an answer.
        /// </summary>
        private static Dictionary<int, string> StampableSpeakersByIndex(AttributionAnswer answer) =>
            StampableSpeakersByIndex(answer.Items, answer.ItemIds);

        private static Dictionary<int, string> StampableSpeakersByIndex(
            IReadOnlyList<AttributedItem> answer, IReadOnlyList<Guid?> itemIds)
        {
            var speakers = SpeakersByIndex(answer);
            foreach (var index in speakers.Keys.ToList())
                if (index < 0 || index >= itemIds.Count || itemIds[index] is null)
                    speakers.Remove(index);
            return speakers;
        }

        /// <summary>
        /// The answer as index→speaker. The parser already keeps the first answer per index, so a
        /// later duplicate cannot overwrite it here either.
        /// </summary>
        private static Dictionary<int, string> SpeakersByIndex(IReadOnlyList<AttributedItem> answer)
        {
            var speakers = new Dictionary<int, string>(answer.Count);
            foreach (var item in answer)
                speakers.TryAdd(item.Index, item.Speaker.Trim());
            return speakers;
        }

        /// <summary>
        /// True when this index names nobody the apply can stamp: unanswered, the wire sentinel, or
        /// the narrator token with no link behind it.
        /// </summary>
        private static bool LeavesUnstamped(
            int index, IReadOnlyDictionary<int, string> speakers, NarratorIdentity narrator) =>
            !speakers.TryGetValue(index, out var speaker) ||
            AttributionWire.IsUnknownSpeaker(speaker) ||
            IsUnlinkedNarrator(speaker, narrator);

        /// <summary>The narrator token with no link behind it: nobody to stamp, so it means unknown.</summary>
        private static bool IsUnlinkedNarrator(string speaker, NarratorIdentity narrator) =>
            AttributionWire.IsNarrator(speaker) && !narrator.IsLinked;

        /// <summary>
        /// True when an unlisted speaker's name appears as a whole word in this paragraph's own
        /// narration items — an attribution tag such as "…and Tathar said,". That is direct textual
        /// evidence, so the name is a first appearance rather than a hallucination and the answer
        /// need not escalate: the final accept will create the character.
        /// <para>
        /// Only narration counts. A name inside dialog is usually a vocative ("Well, John?"), where
        /// the named character is the one addressed, not the speaker — exactly the case escalation
        /// exists to catch.
        /// </para>
        /// </summary>
        private static bool IsAttestedInNarration(string speaker, IReadOnlyList<ContextItem> items)
        {
            if (speaker.Length == 0)
                return false;

            foreach (var item in items)
            {
                if (item.IsDialog)
                    continue;
                if (ContainsWholeWord(item.Text, speaker))
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
    }
}
