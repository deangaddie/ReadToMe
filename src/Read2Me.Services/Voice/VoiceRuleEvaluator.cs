using System;
using System.Collections.Generic;

namespace Read2Me.Services.Voice
{
    /// <summary>
    /// Pure, DB-free evaluator that selects a VoiceId for one ParagraphItem given
    /// the character's ordered rules and the item's StoryPosition.
    ///
    /// Algorithm: sort by Rank ascending, evaluate each rule, last passing rule wins.
    /// The default rule (null anchors, not dangling) always passes, guaranteeing a result
    /// whenever at least one rule exists.
    /// </summary>
    public static class VoiceRuleEvaluator
    {
        public static Guid? Evaluate(IReadOnlyList<RuleInput> rules, StoryPosition position)
        {
            if (rules.Count == 0) return null;

            // Sort ascending by Rank (Ordinal = BINARY collation matches the DB ordering).
            var sorted = new List<RuleInput>(rules);
            sorted.Sort((a, b) => string.CompareOrdinal(a.Rank, b.Rank));

            Guid? winner = null;
            foreach (var rule in sorted)
            {
                if (rule.IsDangling) continue;

                // Null From = open start (-∞); null To = open end (+∞).
                var afterFrom = rule.From is null || position.CompareTo(rule.From.Value) >= 0;
                var beforeTo  = rule.To   is null || position.CompareTo(rule.To.Value)   <= 0;

                if (afterFrom && beforeTo)
                    winner = rule.VoiceId;
            }

            return winner;
        }
    }
}
