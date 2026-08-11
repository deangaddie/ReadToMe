using Read2Me.Data;
using Read2Me.Data.Entities;
using Read2Me.Services;
using Read2Me.Services.Characters;
using Read2Me.Services.Llm;
using Xunit;

namespace Read2Me.Tests.Services.Characters
{
    /// <summary>
    /// Escalation semantics over a per-item answer. Every case is an answer judged against the
    /// paragraph's own items (ADR 0005: the model names existing items, it never re-splits), so the
    /// fixtures build both halves — the items the prompt numbered, and what the model said about them.
    /// </summary>
    public class ItemAttributionEscalationTests
    {
        private static readonly IReadOnlyList<Character> Characters =
        [
            new Character
            {
                Name = "Queen of Hearts",
                Aliases = [new CharacterAlias { Name = "the Queen" }],
            },
            new Character { Name = "Alice" },
        ];

        /// <summary>The book Alice narrates as well as appears in — "narrator" means her.</summary>
        private static readonly NarratorIdentity LinkedToAlice = new(Guid.NewGuid(), "Alice", true);

        // ── The paragraph's items, as the prompt numbered them ─────────────────

        private static ContextItem DialogItem(string text = "“Hi.”", string speaker = "unknown") =>
            new(Guid.NewGuid(), text, AttributionWire.Dialog, speaker);

        private static ContextItem NarrationItem(string text = "she said.") =>
            new(Guid.NewGuid(), text, AttributionWire.Narration, AttributionWire.Narrator);

        private static IReadOnlyList<ContextItem> Items(params ContextItem[] items) => items;

        /// <summary>The commonest shape: one dialog item at index 0.</summary>
        private static readonly IReadOnlyList<ContextItem> OneDialog = Items(DialogItem());

        // ── The answer ────────────────────────────────────────────────────────

        private static AttributedItem Says(int index, string speaker, string? voice = "") =>
            new(index, speaker, voice);

        private static IReadOnlyList<AttributedItem> Answer(params AttributedItem[] answered) => answered;

        /// <summary>Unlinked is the baseline: the seed Narrator row narrates.</summary>
        private static EscalationTrigger DeriveTrigger(
            IReadOnlyList<AttributedItem> answer, IReadOnlyList<ContextItem> items,
            IReadOnlyList<Character>? characters = null) =>
            ItemAttributionEscalation.DeriveTrigger(
                answer, items, characters ?? Characters, NarratorIdentity.Unlinked);

        /// <summary>Four dialog items, so an agreement fixture can name any index 0..3.</summary>
        private static readonly IReadOnlyList<ContextItem> FourDialog =
            Items(DialogItem(), DialogItem(), DialogItem(), DialogItem());

        /// <summary>
        /// Both samples answered one ask about one paragraph, so they share its index→id map.
        /// </summary>
        private static bool AnswersAgree(
            IReadOnlyList<AttributedItem> a, IReadOnlyList<AttributedItem> b,
            IReadOnlyList<ContextItem>? items = null, NarratorIdentity? narrator = null) =>
            ItemAttributionEscalation.AnswersAgree(
                AttributionAnswer.For(a, items ?? FourDialog),
                AttributionAnswer.For(b, items ?? FourDialog),
                Characters, narrator ?? NarratorIdentity.Unlinked);

        // --- DeriveTrigger ---

        [Fact]
        public void AllKnownSpeakers_TriggerNone()
        {
            var trigger = DeriveTrigger(
                Answer(Says(0, "Alice"), Says(2, "Queen of Hearts")),
                Items(DialogItem(), NarrationItem(), DialogItem()));
            Assert.Equal(EscalationTrigger.None, trigger);
        }

        [Fact]
        public void AliasSpeaker_CountsAsKnown()
        {
            Assert.Equal(EscalationTrigger.None, DeriveTrigger(Answer(Says(0, "the queen ")), OneDialog));
        }

        [Fact]
        public void ParagraphWithNoDialogItems_TriggerNone()
        {
            // Nothing to attribute: a pure-narration paragraph is answered by saying nothing.
            Assert.Equal(EscalationTrigger.None,
                DeriveTrigger(Answer(), Items(NarrationItem(), NarrationItem())));
        }

        /// <summary>
        /// The frozen-boundary case the old segment wire could not express: the model answered some
        /// items and simply did not mention this one. Unanswered ≡ unknown — both mean the rung
        /// failed to name a speaker, and the item stays unattributed either way.
        /// </summary>
        [Fact]
        public void DialogItemNotAnsweredAtAll_TriggerUnknown()
        {
            var items = Items(DialogItem("“Hi.”"), NarrationItem(), DialogItem("“Who's there?”"));

            Assert.Equal(EscalationTrigger.Unknown, DeriveTrigger(Answer(Says(0, "Alice")), items));
            Assert.True(ItemAttributionEscalation.HasUnknownSpeaker(
                Answer(Says(0, "Alice")), items, NarratorIdentity.Unlinked));
        }

        [Fact]
        public void EmptyAnswer_ForADialogParagraph_TriggerUnknown()
        {
            Assert.Equal(EscalationTrigger.Unknown, DeriveTrigger(Answer(), OneDialog));
        }

        /// <summary>An answer on a narration index is ignored, never rejected (spec §2).</summary>
        [Fact]
        public void AnswerOnANarrationIndex_IsIgnored()
        {
            var items = Items(NarrationItem(), DialogItem());

            Assert.Equal(EscalationTrigger.None,
                DeriveTrigger(Answer(Says(0, "Mock Turtle"), Says(1, "Alice")), items));
            Assert.False(ItemAttributionEscalation.HasUnknownSpeaker(
                Answer(Says(0, "unknown"), Says(1, "Alice")), items, NarratorIdentity.Unlinked));
        }

        /// <summary>An index nothing in the paragraph carries is ignored the same way.</summary>
        [Fact]
        public void AnswerOnAnOutOfRangeIndex_IsIgnored()
        {
            Assert.Equal(EscalationTrigger.None,
                DeriveTrigger(Answer(Says(0, "Alice"), Says(9, "Mock Turtle")), OneDialog));
        }

        /// <summary>
        /// Unlinked, a spoken line credited to the narrator is credited to someone by definition not
        /// in the scene, and stamps nobody — so it is unattributed, not a confident answer.
        /// </summary>
        [Fact]
        public void DialogSpokenByNarrator_Unlinked_TriggerUnknown()
        {
            Assert.Equal(EscalationTrigger.Unknown, DeriveTrigger(Answer(Says(0, "narrator")), OneDialog));
        }

        /// <summary>
        /// Status follows the trigger: the same item that escalates as unknown must also read as
        /// leaving the paragraph unattributed, or the answer would score Resolved while escalating.
        /// </summary>
        [Fact]
        public void DialogSpokenByNarrator_CountsAsUnattributed_OnlyWhenUnlinked()
        {
            Assert.True(ItemAttributionEscalation.HasUnknownSpeaker(
                Answer(Says(0, "narrator")), OneDialog, NarratorIdentity.Unlinked));
            Assert.False(ItemAttributionEscalation.HasUnknownSpeaker(
                Answer(Says(0, "narrator")), OneDialog, LinkedToAlice));
        }

        /// <summary>Never UnlistedName: that tier's final accept would create a character named "narrator".</summary>
        [Fact]
        public void DialogSpokenByNarrator_Unlinked_DoesNotOutrankAnUnlistedName()
        {
            var trigger = DeriveTrigger(
                Answer(Says(0, "narrator"), Says(1, "Mock Turtle")),
                Items(DialogItem(), DialogItem()));
            Assert.Equal(EscalationTrigger.UnlistedName, trigger);
        }

        /// <summary>Linked, "narrator" on dialog is a correct answer and must burn no rung.</summary>
        [Fact]
        public void DialogSpokenByNarrator_Linked_TriggerNone()
        {
            Assert.Equal(EscalationTrigger.None, ItemAttributionEscalation.DeriveTrigger(
                Answer(Says(0, "narrator")), OneDialog, Characters, LinkedToAlice));
        }

        /// <summary>The narrator link changes nothing else: ordinary triggers are link-blind.</summary>
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void OtherTriggers_AreUnaffectedByTheLink(bool linked)
        {
            var narrator = linked ? LinkedToAlice : NarratorIdentity.Unlinked;

            Assert.Equal(EscalationTrigger.None, ItemAttributionEscalation.DeriveTrigger(
                Answer(Says(0, "Alice")), Items(DialogItem(), NarrationItem()), Characters, narrator));
            Assert.Equal(EscalationTrigger.Unknown, ItemAttributionEscalation.DeriveTrigger(
                Answer(Says(0, "unknown")), OneDialog, Characters, narrator));
            Assert.Equal(EscalationTrigger.UnlistedName, ItemAttributionEscalation.DeriveTrigger(
                Answer(Says(0, "Mock Turtle")), OneDialog, Characters, narrator));
        }

        [Fact]
        public void UnknownDialogSpeaker_TriggerUnknown()
        {
            var trigger = DeriveTrigger(
                Answer(Says(0, "Alice"), Says(1, "unknown")), Items(DialogItem(), DialogItem()));
            Assert.Equal(EscalationTrigger.Unknown, trigger);
        }

        [Fact]
        public void UnlistedDialogSpeaker_TriggerUnlistedName()
        {
            Assert.Equal(EscalationTrigger.UnlistedName, DeriveTrigger(Answer(Says(0, "Mock Turtle")), OneDialog));
        }

        [Fact]
        public void UnlistedSpeakerNamedByNarrationTag_TriggerNone()
        {
            // "…and Tathar said," is direct textual evidence for a first appearance, so the answer
            // is confident and lands now — the character is created on apply — rather than costing
            // a walk down the whole escalation chain.
            var trigger = DeriveTrigger(
                Answer(Says(1, "Tathar")),
                Items(NarrationItem("Borric motioned for the boys to approach, and Tathar said, "),
                      DialogItem("“Which of you found this outworlder?”")));
            Assert.Equal(EscalationTrigger.None, trigger);
        }

        [Fact]
        public void UnlistedSpeakerNamedByPossessiveInNarration_TriggerNone()
        {
            var trigger = DeriveTrigger(
                Answer(Says(1, "Tathar")),
                Items(NarrationItem("Tathar’s gaze did not waver. "), DialogItem()));
            Assert.Equal(EscalationTrigger.None, trigger);
        }

        [Fact]
        public void UnlistedSpeakerNamedOnlyInsideDialog_TriggerUnlistedName()
        {
            // A name inside a quote is usually a vocative — the character addressed, not the
            // speaker. Exactly what escalation exists to catch, so narration alone attests.
            var trigger = DeriveTrigger(
                Answer(Says(0, "Mock Turtle")), Items(DialogItem("“Well, Gryphon?”")));
            Assert.Equal(EscalationTrigger.UnlistedName, trigger);
        }

        [Fact]
        public void UnlistedSpeakerMatchingOnlyPartOfANarrationWord_TriggerUnlistedName()
        {
            // "Tom" must not be attested by "Tomas" — whole-word match only.
            var trigger = DeriveTrigger(
                Answer(Says(1, "Tom")),
                Items(NarrationItem("Tomas began haltingly. "), DialogItem()));
            Assert.Equal(EscalationTrigger.UnlistedName, trigger);
        }

        [Fact]
        public void UnknownSpeakerAlongsideAttestedUnlistedSpeaker_TriggerUnknown()
        {
            // Attestation clears the unlisted name, so the unattributed item is what is left.
            var trigger = DeriveTrigger(
                Answer(Says(1, "Tathar"), Says(2, "unknown")),
                Items(NarrationItem("Tathar said, "), DialogItem(), DialogItem()));
            Assert.Equal(EscalationTrigger.Unknown, trigger);
        }

        [Fact]
        public void UnlistedBeatsUnknown()
        {
            var trigger = DeriveTrigger(
                Answer(Says(0, "unknown"), Says(1, "Mock Turtle")), Items(DialogItem(), DialogItem()));
            Assert.Equal(EscalationTrigger.UnlistedName, trigger);
        }

        /// <summary>Unanswered loses to an unlisted name too — the precedence is over the whole answer.</summary>
        [Fact]
        public void UnlistedBeatsUnanswered()
        {
            var trigger = DeriveTrigger(Answer(Says(1, "Mock Turtle")), Items(DialogItem(), DialogItem()));
            Assert.Equal(EscalationTrigger.UnlistedName, trigger);
        }

        // --- AnswersAgree ---

        [Fact]
        public void IdenticalAnswers_Agree()
        {
            Assert.True(AnswersAgree(
                Answer(Says(0, "Alice"), Says(2, "Queen of Hearts")),
                Answer(Says(0, "Alice"), Says(2, "Queen of Hearts"))));
        }

        [Fact]
        public void AliasVsCanonicalSpeaker_Agrees()
        {
            Assert.True(AnswersAgree(Answer(Says(0, "the Queen")), Answer(Says(0, "Queen of Hearts"))));
        }

        [Fact]
        public void VoiceInstructions_Ignored()
        {
            Assert.True(AnswersAgree(
                Answer(Says(0, "Alice", "warm")), Answer(Says(0, "Alice", "cold"))));
            Assert.True(AnswersAgree(
                Answer(Says(0, "Alice", null)), Answer(Says(0, "Alice", "cold"))));
        }

        [Fact]
        public void BothUnknown_Agree()
        {
            Assert.True(AnswersAgree(Answer(Says(0, "unknown")), Answer(Says(0, "unknown"))));
        }

        [Fact]
        public void BothEmpty_Agree()
        {
            Assert.True(AnswersAgree(Answer(), Answer()));
        }

        /// <summary>
        /// Coverage is part of the comparison: one sample thinks index 1 has a speaker and the other
        /// says nothing about it, which is a disagreement about the paragraph, not about a name.
        /// </summary>
        [Fact]
        public void IndexAnsweredByOnlyOneSample_Disagrees()
        {
            Assert.False(AnswersAgree(
                Answer(Says(0, "Alice"), Says(1, "Alice")), Answer(Says(0, "Alice"))));
            Assert.False(AnswersAgree(
                Answer(Says(0, "Alice")), Answer(Says(0, "Alice"), Says(1, "Alice"))));
        }

        /// <summary>Same count, different indices — a size-only comparison would pass this.</summary>
        [Fact]
        public void SameCountDifferentIndices_Disagrees()
        {
            Assert.False(AnswersAgree(Answer(Says(0, "Alice")), Answer(Says(1, "Alice"))));
        }

        [Fact]
        public void DifferentSpeaker_Disagrees()
        {
            Assert.False(AnswersAgree(Answer(Says(0, "Alice")), Answer(Says(0, "Queen of Hearts"))));
        }

        [Fact]
        public void UnknownVsNamed_Disagrees()
        {
            Assert.False(AnswersAgree(Answer(Says(0, "unknown")), Answer(Says(0, "Alice"))));
        }

        [Fact]
        public void UnlistedNames_CompareRaw()
        {
            Assert.True(AnswersAgree(Answer(Says(0, "Mock Turtle")), Answer(Says(0, "mock turtle"))));
            Assert.False(AnswersAgree(Answer(Says(0, "Mock Turtle")), Answer(Says(0, "Gryphon"))));
        }

        /// <summary>
        /// The case that bites without narrator-aware canonicalization: self-consistency resamples at
        /// temperature, so one sample answers the wire token and the other the linked character's own
        /// name. Both are right — scoring them Inconsistent burns a stronger rung on two correct
        /// answers, on exactly the books the link exists for.
        /// </summary>
        [Fact]
        public void NarratorVsLinkedCharacterName_Agrees_WhenLinked()
        {
            Assert.True(AnswersAgree(
                Answer(Says(0, "narrator")), Answer(Says(0, "Alice")), narrator: LinkedToAlice));
        }

        /// <summary>
        /// Answers on narration indices are ignored here as everywhere else (spec §2): one sample
        /// volunteering a speaker for narration the other left alone is not a disagreement about
        /// anything that can be applied, and escalating over it would burn a rung for nothing.
        /// </summary>
        [Fact]
        public void NarrationIndexAnsweredByOnlyOneSample_StillAgrees()
        {
            var items = Items(DialogItem(), NarrationItem());

            Assert.True(AnswersAgree(
                Answer(Says(0, "Alice"), Says(1, "narrator")), Answer(Says(0, "Alice")), items));
        }

        /// <summary>An index past the paragraph's items is nobody's speaker, so it cannot disagree either.</summary>
        [Fact]
        public void OutOfRangeIndexAnsweredByOnlyOneSample_StillAgrees()
        {
            Assert.True(AnswersAgree(
                Answer(Says(0, "Alice"), Says(9, "Gryphon")), Answer(Says(0, "Alice"))));
        }

        /// <summary>Unlinked the token names nobody, so it cannot agree with a real character.</summary>
        [Fact]
        public void NarratorVsCharacterName_Disagrees_WhenUnlinked()
        {
            Assert.False(AnswersAgree(Answer(Says(0, "narrator")), Answer(Says(0, "Alice"))));
        }

        [Fact]
        public void BothNarrator_Agree_EitherWay()
        {
            Assert.True(AnswersAgree(Answer(Says(0, "narrator")), Answer(Says(0, "Narrator "))));
            Assert.True(AnswersAgree(
                Answer(Says(0, "narrator")), Answer(Says(0, "Narrator ")), narrator: LinkedToAlice));
        }
    }
}
