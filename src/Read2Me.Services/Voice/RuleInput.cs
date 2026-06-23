namespace Read2Me.Services.Voice
{
    /// <summary>
    /// Caller-resolved view of a VoiceRule for evaluation. DB lookups happen before this point;
    /// the evaluator itself is pure (no DB access).
    /// </summary>
    /// <param name="VoiceId">The target voice.</param>
    /// <param name="Rank">Fractional rank string (BINARY-collated); lower Rank = evaluated first.</param>
    /// <param name="IsDefault">True for the pinned floor rule with null anchors.</param>
    /// <param name="IsDangling">True when the anchor node no longer exists — rule is skipped.</param>
    /// <param name="From">Resolved subtree-minimum position; null means open start (−∞).</param>
    /// <param name="To">Resolved subtree-maximum position; null means open end (+∞).</param>
    public sealed record RuleInput(
        Guid VoiceId,
        string Rank,
        bool IsDefault,
        bool IsDangling,
        StoryPosition? From,
        StoryPosition? To);
}
