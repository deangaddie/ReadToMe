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

        // --- DeriveTrigger ---

        [Fact]
        public void AllKnownSpeakers_TriggerNone()
        {
            var trigger = SegmentEscalation.DeriveTrigger(
                [Dialog("Alice"), Narration(), Dialog("Queen of Hearts")], Characters);
            Assert.Equal(EscalationTrigger.None, trigger);
        }

        [Fact]
        public void AliasSpeaker_CountsAsKnown()
        {
            var trigger = SegmentEscalation.DeriveTrigger([Dialog("the queen ")], Characters);
            Assert.Equal(EscalationTrigger.None, trigger);
        }

        [Fact]
        public void AllNarration_TriggerNone()
        {
            // A dialog-queued paragraph answered as pure narration is valid — the
            // re-segmentation overrides the earlier classifier.
            var trigger = SegmentEscalation.DeriveTrigger([Narration(), Narration()], Characters);
            Assert.Equal(EscalationTrigger.None, trigger);
        }

        [Fact]
        public void DialogSpokenByNarrator_TriggerNone()
        {
            var trigger = SegmentEscalation.DeriveTrigger([Dialog("narrator")], Characters);
            Assert.Equal(EscalationTrigger.None, trigger);
        }

        [Fact]
        public void UnknownDialogSpeaker_TriggerUnknown()
        {
            var trigger = SegmentEscalation.DeriveTrigger(
                [Dialog("Alice"), Dialog("unknown")], Characters);
            Assert.Equal(EscalationTrigger.Unknown, trigger);
        }

        [Fact]
        public void UnlistedDialogSpeaker_TriggerUnlistedName()
        {
            var trigger = SegmentEscalation.DeriveTrigger([Dialog("Mock Turtle")], Characters);
            Assert.Equal(EscalationTrigger.UnlistedName, trigger);
        }

        [Fact]
        public void UnlistedSpeakerNamedByNarrationTag_TriggerNone()
        {
            // "…and Tathar said," is direct textual evidence for a first appearance, so the answer
            // is confident and lands now — the character is created on apply — rather than costing
            // a walk down the whole escalation chain.
            var trigger = SegmentEscalation.DeriveTrigger(
                [Narration("Borric motioned for the boys to approach, and Tathar said, "),
                 Dialog("Tathar", "“Which of you found this outworlder?”")],
                Characters);
            Assert.Equal(EscalationTrigger.None, trigger);
        }

        [Fact]
        public void UnlistedSpeakerNamedByPossessiveInNarration_TriggerNone()
        {
            var trigger = SegmentEscalation.DeriveTrigger(
                [Narration("Tathar’s gaze did not waver. "), Dialog("Tathar")], Characters);
            Assert.Equal(EscalationTrigger.None, trigger);
        }

        [Fact]
        public void UnlistedSpeakerNamedOnlyInsideDialog_TriggerUnlistedName()
        {
            // A name inside a quote is usually a vocative — the character addressed, not the
            // speaker. Exactly what escalation exists to catch, so narration alone attests.
            var trigger = SegmentEscalation.DeriveTrigger(
                [Dialog("Mock Turtle", "“Well, Gryphon?”")], Characters);
            Assert.Equal(EscalationTrigger.UnlistedName, trigger);
        }

        [Fact]
        public void UnlistedSpeakerMatchingOnlyPartOfANarrationWord_TriggerUnlistedName()
        {
            // "Tom" must not be attested by "Tomas" — whole-word match only.
            var trigger = SegmentEscalation.DeriveTrigger(
                [Narration("Tomas began haltingly. "), Dialog("Tom")], Characters);
            Assert.Equal(EscalationTrigger.UnlistedName, trigger);
        }

        [Fact]
        public void UnknownSpeakerAlongsideAttestedUnlistedSpeaker_TriggerUnknown()
        {
            // Attestation clears the unlisted name, so the unattributed segment is what is left.
            var trigger = SegmentEscalation.DeriveTrigger(
                [Narration("Tathar said, "), Dialog("Tathar"), Dialog("unknown")], Characters);
            Assert.Equal(EscalationTrigger.Unknown, trigger);
        }

        [Fact]
        public void UnlistedBeatsUnknown()
        {
            var trigger = SegmentEscalation.DeriveTrigger(
                [Dialog("unknown"), Dialog("Mock Turtle")], Characters);
            Assert.Equal(EscalationTrigger.UnlistedName, trigger);
        }

        // --- AnswersAgree ---

        [Fact]
        public void IdenticalAnswers_Agree()
        {
            var a = Answer(Dialog("Alice", "“Hi.”"), Narration());
            var b = Answer(Dialog("Alice", "“Hi.”"), Narration());
            Assert.True(SegmentEscalation.AnswersAgree(a, b, Characters));
        }

        [Fact]
        public void NormalizedTextDrift_StillAgrees()
        {
            var a = Answer(Dialog("Alice", "“Sentence first—verdict afterwards.”"));
            var b = Answer(Dialog("Alice", "\"Sentence  first--verdict afterwards.\""));
            Assert.True(SegmentEscalation.AnswersAgree(a, b, Characters));
        }

        [Fact]
        public void AliasVsCanonicalSpeaker_Agrees()
        {
            var a = Answer(Dialog("the Queen"));
            var b = Answer(Dialog("Queen of Hearts"));
            Assert.True(SegmentEscalation.AnswersAgree(a, b, Characters));
        }

        [Fact]
        public void VoiceInstructions_Ignored()
        {
            var a = Answer(Dialog("Alice", voice: "warm"));
            var b = Answer(Dialog("Alice", voice: "cold"));
            Assert.True(SegmentEscalation.AnswersAgree(a, b, Characters));
        }

        [Fact]
        public void BothUnknown_Agree()
        {
            Assert.True(SegmentEscalation.AnswersAgree(
                Answer(Dialog("unknown")), Answer(Dialog("unknown")), Characters));
        }

        [Fact]
        public void DifferentSegmentCount_Disagrees()
        {
            var a = Answer(Dialog("Alice"), Narration());
            var b = Answer(Dialog("Alice"));
            Assert.False(SegmentEscalation.AnswersAgree(a, b, Characters));
        }

        [Fact]
        public void DifferentText_Disagrees()
        {
            var a = Answer(Dialog("Alice", "“Hi there.”"));
            var b = Answer(Dialog("Alice", "“Hi.”"));
            Assert.False(SegmentEscalation.AnswersAgree(a, b, Characters));
        }

        [Fact]
        public void DifferentType_Disagrees()
        {
            var a = Answer(new AttributionSegment("Go.", AttributionSegmentType.Dialog, "Alice", ""));
            var b = Answer(new AttributionSegment("Go.", AttributionSegmentType.Narration, "narrator", ""));
            Assert.False(SegmentEscalation.AnswersAgree(a, b, Characters));
        }

        [Fact]
        public void DifferentSpeaker_Disagrees()
        {
            var a = Answer(Dialog("Alice"));
            var b = Answer(Dialog("Queen of Hearts"));
            Assert.False(SegmentEscalation.AnswersAgree(a, b, Characters));
        }

        [Fact]
        public void UnknownVsNamed_Disagrees()
        {
            var a = Answer(Dialog("unknown"));
            var b = Answer(Dialog("Alice"));
            Assert.False(SegmentEscalation.AnswersAgree(a, b, Characters));
        }

        [Fact]
        public void UnlistedNames_CompareRaw()
        {
            Assert.True(SegmentEscalation.AnswersAgree(
                Answer(Dialog("Mock Turtle")), Answer(Dialog("mock turtle")), Characters));
            Assert.False(SegmentEscalation.AnswersAgree(
                Answer(Dialog("Mock Turtle")), Answer(Dialog("Gryphon")), Characters));
        }

        // --- LosesDialog ---

        private static ContextSegment PriorDialog(string speaker = "Alice", string text = "“Hi.”") =>
            new(text, SegmentWire.Dialog, speaker);

        private static ContextSegment PriorNarration(string text = "she said.") =>
            new(text, SegmentWire.Narration, SegmentWire.Narrator);

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
                [PriorDialog(SegmentWire.Unknown)], Answer(Narration("“Hi.”"))));
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
            Assert.Equal(EscalationTrigger.None, SegmentEscalation.DeriveTrigger(answer, Characters));
            Assert.False(SegmentEscalation.HasUnknownSpeaker(answer));
        }
    }
}
