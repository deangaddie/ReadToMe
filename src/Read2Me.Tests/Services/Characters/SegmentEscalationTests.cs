using Read2Me.Data;
using Read2Me.Data.Entities;
using Read2Me.Services;
using Read2Me.Services.Characters;
using Read2Me.Services.Llm;
using Xunit;

namespace Read2Me.Tests.Services.Characters
{
    public class SegmentEscalationTests
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

        private static AttributionSegment Dialog(string speaker, string text = "“Hi.”", string voice = "") =>
            new(text, AttributionSegmentType.Dialog, speaker, voice);

        private static AttributionSegment Narration(string text = "she said.") =>
            new(text, AttributionSegmentType.Narration, "narrator", "");

        /// <summary>An answer is compared as its segment list — reasoning plays no part.</summary>
        private static IReadOnlyList<AttributionSegment> Answer(params AttributionSegment[] segments) =>
            segments;

        /// <summary>The book Alice narrates as well as appears in — "narrator" means her.</summary>
        private static readonly NarratorIdentity LinkedToAlice =
            new(Guid.NewGuid(), "Alice", true);

        // Unlinked is the baseline for every test that predates the narrator link: the seed Narrator
        // row narrates, exactly as before.
        private static EscalationTrigger DeriveTrigger(
            IReadOnlyList<AttributionSegment> segments, IReadOnlyList<Character> characters) =>
            SegmentEscalation.DeriveTrigger(segments, characters, NarratorIdentity.Unlinked);

        private static bool AnswersAgree(
            IReadOnlyList<AttributionSegment> a, IReadOnlyList<AttributionSegment> b,
            IReadOnlyList<Character> characters) =>
            SegmentEscalation.AnswersAgree(a, b, characters, NarratorIdentity.Unlinked);

        // --- DeriveTrigger ---

        [Fact]
        public void AllKnownSpeakers_TriggerNone()
        {
            var trigger = DeriveTrigger(
                [Dialog("Alice"), Narration(), Dialog("Queen of Hearts")], Characters);
            Assert.Equal(EscalationTrigger.None, trigger);
        }

        [Fact]
        public void AliasSpeaker_CountsAsKnown()
        {
            var trigger = DeriveTrigger([Dialog("the queen ")], Characters);
            Assert.Equal(EscalationTrigger.None, trigger);
        }

        [Fact]
        public void AllNarration_TriggerNone()
        {
            // A dialog-queued paragraph answered as pure narration is valid — the
            // re-segmentation overrides the earlier classifier.
            var trigger = DeriveTrigger([Narration(), Narration()], Characters);
            Assert.Equal(EscalationTrigger.None, trigger);
        }

        /// <summary>
        /// Unlinked, a spoken line credited to the narrator is credited to someone by definition not
        /// in the scene, and stamps nobody — so it is unattributed, not a confident answer. This used
        /// to be exempted outright, which is the narration-swallow shape: the item looked attributed,
        /// scored as certainty, and never re-queued.
        /// </summary>
        [Fact]
        public void DialogSpokenByNarrator_Unlinked_TriggerUnknown()
        {
            var trigger = DeriveTrigger([Dialog("narrator")], Characters);
            Assert.Equal(EscalationTrigger.Unknown, trigger);
        }

        /// <summary>
        /// Status follows the trigger: the same segment that escalates as unknown must also read as
        /// leaving the paragraph unattributed, or the answer would score Resolved while escalating.
        /// </summary>
        [Fact]
        public void DialogSpokenByNarrator_CountsAsUnattributed_OnlyWhenUnlinked()
        {
            Assert.True(SegmentEscalation.HasUnknownSpeaker([Dialog("narrator")], NarratorIdentity.Unlinked));
            Assert.False(SegmentEscalation.HasUnknownSpeaker([Dialog("narrator")], LinkedToAlice));
        }

        /// <summary>Never UnlistedName: that tier's final accept would create a character named "narrator".</summary>
        [Fact]
        public void DialogSpokenByNarrator_Unlinked_DoesNotOutrankAnUnlistedName()
        {
            var trigger = DeriveTrigger([Dialog("narrator"), Dialog("Mock Turtle")], Characters);
            Assert.Equal(EscalationTrigger.UnlistedName, trigger);
        }

        /// <summary>Linked, "narrator" on dialog is a correct answer and must burn no rung.</summary>
        [Fact]
        public void DialogSpokenByNarrator_Linked_TriggerNone()
        {
            var trigger = SegmentEscalation.DeriveTrigger([Dialog("narrator")], Characters, LinkedToAlice);
            Assert.Equal(EscalationTrigger.None, trigger);
        }

        /// <summary>
        /// The converse, and a confirmed non-change, asserted end to end from the wire: a narration
        /// segment answered with the linked character's own name is accepted with zero code — the
        /// parser force-writes the wire token, so it stamps <c>narrator</c>, raises no trigger, and
        /// is not read as a re-segmentation. Naming the narrator on narration is the model agreeing
        /// with the identity line; escalating there would escalate hardest where it understood best.
        /// </summary>
        [Fact]
        public void NarrationAnsweredWithTheLinkedCharactersName_IsAcceptedAsNarration()
        {
            var raw = """
                { "reasoning": "r", "segments": [
                  { "text": "she said.", "type": "narration", "speaker": "Alice", "voice_instructions": "" },
                  { "text": "“Hi.”", "type": "dialog", "speaker": "Alice", "voice_instructions": "" }
                ] }
                """;

            Assert.True(SegmentAttributionParser.TryParse(raw, out var parsed));
            var answer = parsed.Segments;

            Assert.Equal("narrator", answer[0].Speaker);
            Assert.Equal(EscalationTrigger.None,
                SegmentEscalation.DeriveTrigger(answer, Characters, LinkedToAlice));
            Assert.False(SegmentEscalation.HasUnknownSpeaker(answer, LinkedToAlice));
            // Not a re-segmentation signal: the prior split's dialog survives the answer.
            Assert.False(SegmentEscalation.LosesDialog([PriorNarration(), PriorDialog()], answer));
        }

        /// <summary>The deleted exemption changes nothing else: ordinary triggers are link-blind.</summary>
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void OtherTriggers_AreUnaffectedByTheLink(bool linked)
        {
            var narrator = linked ? LinkedToAlice : NarratorIdentity.Unlinked;

            Assert.Equal(EscalationTrigger.None,
                SegmentEscalation.DeriveTrigger([Dialog("Alice"), Narration()], Characters, narrator));
            Assert.Equal(EscalationTrigger.Unknown,
                SegmentEscalation.DeriveTrigger([Dialog("unknown")], Characters, narrator));
            Assert.Equal(EscalationTrigger.UnlistedName,
                SegmentEscalation.DeriveTrigger([Dialog("Mock Turtle")], Characters, narrator));
        }

        [Fact]
        public void UnknownDialogSpeaker_TriggerUnknown()
        {
            var trigger = DeriveTrigger(
                [Dialog("Alice"), Dialog("unknown")], Characters);
            Assert.Equal(EscalationTrigger.Unknown, trigger);
        }

        [Fact]
        public void UnlistedDialogSpeaker_TriggerUnlistedName()
        {
            var trigger = DeriveTrigger([Dialog("Mock Turtle")], Characters);
            Assert.Equal(EscalationTrigger.UnlistedName, trigger);
        }

        [Fact]
        public void UnlistedSpeakerNamedByNarrationTag_TriggerNone()
        {
            // "…and Tathar said," is direct textual evidence for a first appearance, so the answer
            // is confident and lands now — the character is created on apply — rather than costing
            // a walk down the whole escalation chain.
            var trigger = DeriveTrigger(
                [Narration("Borric motioned for the boys to approach, and Tathar said, "),
                 Dialog("Tathar", "“Which of you found this outworlder?”")],
                Characters);
            Assert.Equal(EscalationTrigger.None, trigger);
        }

        [Fact]
        public void UnlistedSpeakerNamedByPossessiveInNarration_TriggerNone()
        {
            var trigger = DeriveTrigger(
                [Narration("Tathar’s gaze did not waver. "), Dialog("Tathar")], Characters);
            Assert.Equal(EscalationTrigger.None, trigger);
        }

        [Fact]
        public void UnlistedSpeakerNamedOnlyInsideDialog_TriggerUnlistedName()
        {
            // A name inside a quote is usually a vocative — the character addressed, not the
            // speaker. Exactly what escalation exists to catch, so narration alone attests.
            var trigger = DeriveTrigger(
                [Dialog("Mock Turtle", "“Well, Gryphon?”")], Characters);
            Assert.Equal(EscalationTrigger.UnlistedName, trigger);
        }

        [Fact]
        public void UnlistedSpeakerMatchingOnlyPartOfANarrationWord_TriggerUnlistedName()
        {
            // "Tom" must not be attested by "Tomas" — whole-word match only.
            var trigger = DeriveTrigger(
                [Narration("Tomas began haltingly. "), Dialog("Tom")], Characters);
            Assert.Equal(EscalationTrigger.UnlistedName, trigger);
        }

        [Fact]
        public void UnknownSpeakerAlongsideAttestedUnlistedSpeaker_TriggerUnknown()
        {
            // Attestation clears the unlisted name, so the unattributed segment is what is left.
            var trigger = DeriveTrigger(
                [Narration("Tathar said, "), Dialog("Tathar"), Dialog("unknown")], Characters);
            Assert.Equal(EscalationTrigger.Unknown, trigger);
        }

        [Fact]
        public void UnlistedBeatsUnknown()
        {
            var trigger = DeriveTrigger(
                [Dialog("unknown"), Dialog("Mock Turtle")], Characters);
            Assert.Equal(EscalationTrigger.UnlistedName, trigger);
        }

        // --- AnswersAgree ---

        [Fact]
        public void IdenticalAnswers_Agree()
        {
            var a = Answer(Dialog("Alice", "“Hi.”"), Narration());
            var b = Answer(Dialog("Alice", "“Hi.”"), Narration());
            Assert.True(AnswersAgree(a, b, Characters));
        }

        [Fact]
        public void NormalizedTextDrift_StillAgrees()
        {
            var a = Answer(Dialog("Alice", "“Sentence first—verdict afterwards.”"));
            var b = Answer(Dialog("Alice", "\"Sentence  first--verdict afterwards.\""));
            Assert.True(AnswersAgree(a, b, Characters));
        }

        [Fact]
        public void AliasVsCanonicalSpeaker_Agrees()
        {
            var a = Answer(Dialog("the Queen"));
            var b = Answer(Dialog("Queen of Hearts"));
            Assert.True(AnswersAgree(a, b, Characters));
        }

        [Fact]
        public void VoiceInstructions_Ignored()
        {
            var a = Answer(Dialog("Alice", voice: "warm"));
            var b = Answer(Dialog("Alice", voice: "cold"));
            Assert.True(AnswersAgree(a, b, Characters));
        }

        [Fact]
        public void BothUnknown_Agree()
        {
            Assert.True(AnswersAgree(
                Answer(Dialog("unknown")), Answer(Dialog("unknown")), Characters));
        }

        [Fact]
        public void DifferentSegmentCount_Disagrees()
        {
            var a = Answer(Dialog("Alice"), Narration());
            var b = Answer(Dialog("Alice"));
            Assert.False(AnswersAgree(a, b, Characters));
        }

        [Fact]
        public void DifferentText_Disagrees()
        {
            var a = Answer(Dialog("Alice", "“Hi there.”"));
            var b = Answer(Dialog("Alice", "“Hi.”"));
            Assert.False(AnswersAgree(a, b, Characters));
        }

        [Fact]
        public void DifferentType_Disagrees()
        {
            var a = Answer(new AttributionSegment("Go.", AttributionSegmentType.Dialog, "Alice", ""));
            var b = Answer(new AttributionSegment("Go.", AttributionSegmentType.Narration, "narrator", ""));
            Assert.False(AnswersAgree(a, b, Characters));
        }

        [Fact]
        public void DifferentSpeaker_Disagrees()
        {
            var a = Answer(Dialog("Alice"));
            var b = Answer(Dialog("Queen of Hearts"));
            Assert.False(AnswersAgree(a, b, Characters));
        }

        [Fact]
        public void UnknownVsNamed_Disagrees()
        {
            var a = Answer(Dialog("unknown"));
            var b = Answer(Dialog("Alice"));
            Assert.False(AnswersAgree(a, b, Characters));
        }

        [Fact]
        public void UnlistedNames_CompareRaw()
        {
            Assert.True(AnswersAgree(
                Answer(Dialog("Mock Turtle")), Answer(Dialog("mock turtle")), Characters));
            Assert.False(AnswersAgree(
                Answer(Dialog("Mock Turtle")), Answer(Dialog("Gryphon")), Characters));
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
            Assert.True(SegmentEscalation.AnswersAgree(
                Answer(Dialog("narrator")), Answer(Dialog("Alice")), Characters, LinkedToAlice));
        }

        /// <summary>Unlinked the token names nobody, so it cannot agree with a real character.</summary>
        [Fact]
        public void NarratorVsCharacterName_Disagrees_WhenUnlinked()
        {
            Assert.False(AnswersAgree(Answer(Dialog("narrator")), Answer(Dialog("Alice")), Characters));
        }

        /// <summary>
        /// Unlinked the token owns nobody, but it is still a distinct answer from a blank speaker —
        /// canonicalizing it to null would have quietly made those two agree.
        /// </summary>
        [Fact]
        public void NarratorVsBlankSpeaker_Disagrees_WhenUnlinked()
        {
            Assert.False(AnswersAgree(Answer(Dialog("narrator")), Answer(Dialog(" ")), Characters));
        }

        [Fact]
        public void BothNarrator_Agree_EitherWay()
        {
            Assert.True(AnswersAgree(Answer(Dialog("narrator")), Answer(Dialog("Narrator ")), Characters));
            Assert.True(SegmentEscalation.AnswersAgree(
                Answer(Dialog("narrator")), Answer(Dialog("Narrator ")), Characters, LinkedToAlice));
        }

        // --- LosesDialog ---

        private static ContextSegment PriorDialog(string speaker = "Alice", string text = "“Hi.”") =>
            new(text, AttributionWire.Dialog, speaker);

        private static ContextSegment PriorNarration(string text = "she said.") =>
            new(text, AttributionWire.Narration, AttributionWire.Narrator);

        [Fact]
        public void DialogFoldedIntoNarration_LosesDialog()
        {
            Assert.True(SegmentEscalation.LosesDialog(
                [PriorNarration(), PriorDialog()],
                Answer(Narration("she said. “Hi.”"))));
        }

        [Fact]
        public void PriorDialogStillDialog_DoesNotLose()
        {
            Assert.False(SegmentEscalation.LosesDialog(
                [PriorNarration(), PriorDialog()],
                Answer(Narration(), Dialog("Alice"))));
        }

        /// <summary>
        /// An unattributed prior dialog segment still counts as dialog — the wire sentinel marks a
        /// missing speaker, not a missing line.
        /// </summary>
        [Fact]
        public void PriorUnknownSpeakerDialog_StillCountsAsDialog()
        {
            Assert.True(SegmentEscalation.LosesDialog(
                [PriorDialog(AttributionWire.Unknown)], Answer(Narration("“Hi.”"))));
        }

        /// <summary>The reverse direction is the re-segmentation doing its job, not a loss.</summary>
        [Fact]
        public void NarrationPriorGainingDialog_DoesNotLose()
        {
            Assert.False(SegmentEscalation.LosesDialog(
                [PriorNarration("she said. “Hi.”")],
                Answer(Narration(), Dialog("Alice"))));
        }

        [Fact]
        public void AllNarrationBothSides_DoesNotLose()
        {
            Assert.False(SegmentEscalation.LosesDialog(
                [PriorNarration()], Answer(Narration())));
        }

        /// <summary>Missing evidence never manufactures a loss.</summary>
        [Fact]
        public void NullPrior_DoesNotLose()
        {
            Assert.False(SegmentEscalation.LosesDialog(null, Answer(Narration())));
        }

        /// <summary>
        /// A lost-dialog answer is invisible to the other two checks — that is exactly why it needs
        /// its own trigger.
        /// </summary>
        [Fact]
        public void LostDialogAnswer_ScoresCleanOnTheOtherChecks()
        {
            var answer = Answer(Narration("she said. “Hi.”"));
            Assert.Equal(EscalationTrigger.None, DeriveTrigger(answer, Characters));
            Assert.False(SegmentEscalation.HasUnknownSpeaker(answer, NarratorIdentity.Unlinked));
        }
    }
}
