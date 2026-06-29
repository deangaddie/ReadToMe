using Read2Me.Data.Entities;

namespace Read2Me.Services.Voice;

public static class AnchorSpanResolver
{
    public static RuleInput Build(VoiceRule rule, NodeOrderTables tables)
    {
        StoryPosition fromPos, toPos;
        bool fromDangling, toDangling;

        if (rule.FromLevel is null || rule.FromNodeId is null)
        {
            fromPos = default;
            fromDangling = false;
        }
        else
        {
            fromDangling = !tables.TryResolve(rule.FromLevel.Value, rule.FromNodeId.Value, isMin: true, out fromPos);
        }

        if (rule.ToLevel is null || rule.ToNodeId is null)
        {
            toPos = default;
            toDangling = false;
        }
        else
        {
            toDangling = !tables.TryResolve(rule.ToLevel.Value, rule.ToNodeId.Value, isMin: false, out toPos);
        }

        var isDangling = (rule.FromNodeId.HasValue && fromDangling) ||
                         (rule.ToNodeId.HasValue && toDangling);

        return new RuleInput(
            rule.VoiceId,
            rule.Rank,
            rule.IsDefault,
            IsDangling: isDangling,
            From: (rule.FromLevel.HasValue && !fromDangling) ? fromPos : null,
            To:   (rule.ToLevel.HasValue   && !toDangling)  ? toPos   : null);
    }
}
