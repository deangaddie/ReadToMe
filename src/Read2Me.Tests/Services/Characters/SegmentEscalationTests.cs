using Read2Me.Data.Entities;
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

        private static SegmentAttributionResult Answer(params AttributionSegment[] segments) =>
            new("reasoning", segments);

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
        public void ReasoningAndVoiceInstructions_Ignored()
        {
            var a = new SegmentAttributionResult("one", [Dialog("Alice", voice: "warm")]);
            var b = new SegmentAttributionResult("two", [Dialog("Alice", voice: "cold")]);
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
    }
}
