using System;
using System.Collections.Generic;
using Read2Me.Services.Voice;
using Xunit;

namespace Read2Me.Tests.Services.Voice
{
    public class VoiceRuleEvaluatorTests
    {
        // ── StoryPosition comparison ──────────────────────────────────────────

        [Fact]
        public void StoryPosition_LaterVolume_BeatsEarlierVolume()
        {
            var earlier = new StoryPosition("a0", "a0", "a0", "a0", "a0");
            var later   = new StoryPosition("a1", "a0", "a0", "a0", "a0");
            Assert.True(later.CompareTo(earlier) > 0);
        }

        [Fact]
        public void StoryPosition_SameVolume_LaterPartWins()
        {
            var earlier = new StoryPosition("a0", "a0", "a0", "a0", "a0");
            var later   = new StoryPosition("a0", "a1", "a0", "a0", "a0");
            Assert.True(later.CompareTo(earlier) > 0);
        }

        [Fact]
        public void StoryPosition_SameVolumeAndPart_LaterChapterWins()
        {
            var earlier = new StoryPosition("a0", "a0", "a0", "a0", "a0");
            var later   = new StoryPosition("a0", "a0", "a1", "a0", "a0");
            Assert.True(later.CompareTo(earlier) > 0);
        }

        [Fact]
        public void StoryPosition_SameUpToChapter_LaterParagraphWins()
        {
            var earlier = new StoryPosition("a0", "a0", "a0", "a0", "a0");
            var later   = new StoryPosition("a0", "a0", "a0", "a1", "a0");
            Assert.True(later.CompareTo(earlier) > 0);
        }

        [Fact]
        public void StoryPosition_SameUpToParagraph_LaterItemWins()
        {
            var earlier = new StoryPosition("a0", "a0", "a0", "a0", "a0");
            var later   = new StoryPosition("a0", "a0", "a0", "a0", "a1");
            Assert.True(later.CompareTo(earlier) > 0);
        }

        [Fact]
        public void StoryPosition_LaterVolumeBeatsDeeperItem()
        {
            // Later volume beats an earlier volume even if all other fields are higher
            var laterVol   = new StoryPosition("a1", "a0", "a0", "a0", "a0");
            var deeperItem = new StoryPosition("a0", "a9", "a9", "a9", "a9");
            Assert.True(laterVol.CompareTo(deeperItem) > 0);
        }

        [Fact]
        public void StoryPosition_Equal_ReturnsZero()
        {
            var p1 = new StoryPosition("a0", "a0", "a0", "a0", "a0");
            var p2 = new StoryPosition("a0", "a0", "a0", "a0", "a0");
            Assert.Equal(0, p1.CompareTo(p2));
        }

        // ── Evaluator: default-only rule ──────────────────────────────────────

        private static readonly Guid VoiceA = Guid.NewGuid();
        private static readonly Guid VoiceB = Guid.NewGuid();
        private static readonly StoryPosition AnyPos = new("a0", "a0", "a0", "a0", "a0");

        private static RuleInput DefaultRule(Guid voiceId) =>
            new(voiceId, Rank: "a0", IsDefault: true, IsDangling: false, From: null, To: null);

        private static RuleInput PositionalRule(Guid voiceId, string rank, StoryPosition? from, StoryPosition? to) =>
            new(voiceId, Rank: rank, IsDefault: false, IsDangling: false, From: from, To: to);

        private static RuleInput DanglingRule(Guid voiceId, string rank) =>
            new(voiceId, Rank: rank, IsDefault: false, IsDangling: true, From: AnyPos, To: null);

        [Fact]
        public void DefaultOnlyRule_AlwaysReturnsDefaultVoice()
        {
            var rules = new List<RuleInput> { DefaultRule(VoiceA) };
            var result = VoiceRuleEvaluator.Evaluate(rules, AnyPos);
            Assert.Equal(VoiceA, result);
        }

        [Fact]
        public void NoRules_ReturnsNull()
        {
            var result = VoiceRuleEvaluator.Evaluate(new List<RuleInput>(), AnyPos);
            Assert.Null(result);
        }

        // ── Last passing rule wins ────────────────────────────────────────────

        [Fact]
        public void TwoPassingRules_HigherRankWins()
        {
            // Both rules match everything. Higher Rank (a1) is last — it wins.
            var rules = new List<RuleInput>
            {
                DefaultRule(VoiceA),                                       // rank a0 — passes
                PositionalRule(VoiceB, "a1", from: null, to: null),       // rank a1 — passes, wins
            };
            var result = VoiceRuleEvaluator.Evaluate(rules, AnyPos);
            Assert.Equal(VoiceB, result);
        }

        // ── From-here-on rule ─────────────────────────────────────────────────

        private static readonly StoryPosition ChapterStart = new("a0", "a0", "a1", "a0", "a0");
        private static readonly StoryPosition BeforeChapter = new("a0", "a0", "a0", "a9", "a9");
        private static readonly StoryPosition AtChapter = new("a0", "a0", "a1", "a0", "a0");
        private static readonly StoryPosition AfterChapter = new("a0", "a0", "a1", "a1", "a0");

        [Fact]
        public void FromHereOn_MatchesAtBoundary()
        {
            var rules = new List<RuleInput>
            {
                DefaultRule(VoiceA),
                PositionalRule(VoiceB, "a1", from: ChapterStart, to: null),
            };
            Assert.Equal(VoiceB, VoiceRuleEvaluator.Evaluate(rules, AtChapter));
        }

        [Fact]
        public void FromHereOn_MatchesAfterBoundary()
        {
            var rules = new List<RuleInput>
            {
                DefaultRule(VoiceA),
                PositionalRule(VoiceB, "a1", from: ChapterStart, to: null),
            };
            Assert.Equal(VoiceB, VoiceRuleEvaluator.Evaluate(rules, AfterChapter));
        }

        [Fact]
        public void FromHereOn_DoesNotMatchBefore()
        {
            var rules = new List<RuleInput>
            {
                DefaultRule(VoiceA),
                PositionalRule(VoiceB, "a1", from: ChapterStart, to: null),
            };
            Assert.Equal(VoiceA, VoiceRuleEvaluator.Evaluate(rules, BeforeChapter));
        }

        // ── Single-node rule ──────────────────────────────────────────────────

        private static readonly StoryPosition ChapterMin = new("a0", "a0", "a1", "a0", "a0");
        private static readonly StoryPosition ChapterMax = new("a0", "a0", "a1", "z9", "z9");
        private static readonly StoryPosition InsideChapter = new("a0", "a0", "a1", "a5", "a3");
        private static readonly StoryPosition OutsideChapter = new("a0", "a0", "a2", "a0", "a0");

        [Fact]
        public void SingleNode_MatchesInsideSpan()
        {
            var rules = new List<RuleInput>
            {
                DefaultRule(VoiceA),
                PositionalRule(VoiceB, "a1", from: ChapterMin, to: ChapterMax),
            };
            Assert.Equal(VoiceB, VoiceRuleEvaluator.Evaluate(rules, InsideChapter));
        }

        [Fact]
        public void SingleNode_MatchesAtFromBoundary()
        {
            var rules = new List<RuleInput>
            {
                DefaultRule(VoiceA),
                PositionalRule(VoiceB, "a1", from: ChapterMin, to: ChapterMax),
            };
            Assert.Equal(VoiceB, VoiceRuleEvaluator.Evaluate(rules, ChapterMin));
        }

        [Fact]
        public void SingleNode_MatchesAtToBoundary()
        {
            var rules = new List<RuleInput>
            {
                DefaultRule(VoiceA),
                PositionalRule(VoiceB, "a1", from: ChapterMin, to: ChapterMax),
            };
            Assert.Equal(VoiceB, VoiceRuleEvaluator.Evaluate(rules, ChapterMax));
        }

        [Fact]
        public void SingleNode_DoesNotMatchOutsideSpan()
        {
            var rules = new List<RuleInput>
            {
                DefaultRule(VoiceA),
                PositionalRule(VoiceB, "a1", from: ChapterMin, to: ChapterMax),
            };
            Assert.Equal(VoiceA, VoiceRuleEvaluator.Evaluate(rules, OutsideChapter));
        }

        // ── Dangling rule ─────────────────────────────────────────────────────

        [Fact]
        public void DanglingRule_IsSkipped_FallsToDefault()
        {
            var rules = new List<RuleInput>
            {
                DefaultRule(VoiceA),
                DanglingRule(VoiceB, "a1"),
            };
            Assert.Equal(VoiceA, VoiceRuleEvaluator.Evaluate(rules, AnyPos));
        }
    }
}
