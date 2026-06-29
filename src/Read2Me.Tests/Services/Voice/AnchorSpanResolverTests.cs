using Read2Me.Data.Entities;
using Read2Me.Data.Enums;
using Read2Me.Services.Voice;
using Xunit;

namespace Read2Me.Tests.Services.Voice;

public class AnchorSpanResolverTests
{
    // ── Helpers ──────────────────────────────────────────────────────────────

    private static NodeOrderTables EmptyTables() => new(
        new Dictionary<Guid, string>(),
        new Dictionary<Guid, (string, string)>(),
        new Dictionary<Guid, (string, string, string)>(),
        new Dictionary<Guid, (string, string, string, string)>(),
        new Dictionary<Guid, StoryPosition>());

    private static VoiceRule Rule(
        Guid voiceId, string rank = "a0",
        bool isDefault = false,
        VoiceAnchorLevel? fromLevel = null, Guid? fromNodeId = null,
        VoiceAnchorLevel? toLevel   = null, Guid? toNodeId   = null) => new()
    {
        Id          = Guid.NewGuid(),
        CharacterId = Guid.NewGuid(),
        VoiceId     = voiceId,
        Rank        = rank,
        IsDefault   = isDefault,
        FromLevel   = fromLevel,
        FromNodeId  = fromNodeId,
        ToLevel     = toLevel,
        ToNodeId    = toNodeId,
    };

    private static readonly string Min = StoryPosition.MinKey;
    private static readonly string Max = StoryPosition.MaxKey;

    // ── Tracer bullet: both-null default rule ─────────────────────────────────

    [Fact]
    public void BothNull_DefaultRule_FromAndToNullNotDangling()
    {
        var voiceId = Guid.NewGuid();
        var rule = Rule(voiceId, isDefault: true);

        var result = AnchorSpanResolver.Build(rule, EmptyTables());

        Assert.Equal(voiceId, result.VoiceId);
        Assert.True(result.IsDefault);
        Assert.False(result.IsDangling);
        Assert.Null(result.From);
        Assert.Null(result.To);
    }

    // ── Field pass-through ────────────────────────────────────────────────────

    [Fact]
    public void FieldPassThrough_VoiceIdRankIsDefault_Preserved()
    {
        var voiceId = Guid.NewGuid();
        var rule = Rule(voiceId, rank: "b5", isDefault: true);

        var result = AnchorSpanResolver.Build(rule, EmptyTables());

        Assert.Equal(voiceId, result.VoiceId);
        Assert.Equal("b5", result.Rank);
        Assert.True(result.IsDefault);
    }

    // ── Volume anchor ─────────────────────────────────────────────────────────

    [Fact]
    public void VolumeAnchor_From_ResolvesSentinels()
    {
        var volId   = Guid.NewGuid();
        var voiceId = Guid.NewGuid();
        var tables  = new NodeOrderTables(
            new Dictionary<Guid, string> { [volId] = "V1" },
            new(), new(), new(), new());

        var rule = Rule(voiceId, fromLevel: VoiceAnchorLevel.Volume, fromNodeId: volId,
                                  toLevel:   VoiceAnchorLevel.Volume, toNodeId:   volId);

        var result = AnchorSpanResolver.Build(rule, tables);

        Assert.False(result.IsDangling);
        Assert.Equal(new StoryPosition("V1", Min, Min, Min, Min), result.From);
        Assert.Equal(new StoryPosition("V1", Max, Max, Max, Max), result.To);
    }

    // ── Part anchor ───────────────────────────────────────────────────────────

    [Fact]
    public void PartAnchor_From_ResolvesSentinels()
    {
        var partId  = Guid.NewGuid();
        var voiceId = Guid.NewGuid();
        var tables  = new NodeOrderTables(
            new(),
            new Dictionary<Guid, (string, string)> { [partId] = ("V1", "P1") },
            new(), new(), new());

        var rule = Rule(voiceId, fromLevel: VoiceAnchorLevel.Part, fromNodeId: partId,
                                  toLevel:   VoiceAnchorLevel.Part, toNodeId:   partId);

        var result = AnchorSpanResolver.Build(rule, tables);

        Assert.False(result.IsDangling);
        Assert.Equal(new StoryPosition("V1", "P1", Min, Min, Min), result.From);
        Assert.Equal(new StoryPosition("V1", "P1", Max, Max, Max), result.To);
    }

    // ── Chapter anchor ────────────────────────────────────────────────────────

    [Fact]
    public void ChapterAnchor_FromAndTo_SameChapter_ResolvesSentinels()
    {
        var chId    = Guid.NewGuid();
        var voiceId = Guid.NewGuid();
        var tables  = new NodeOrderTables(
            new(), new(),
            new Dictionary<Guid, (string, string, string)> { [chId] = ("V1", "P1", "C1") },
            new(), new());

        var rule = Rule(voiceId, fromLevel: VoiceAnchorLevel.Chapter, fromNodeId: chId,
                                  toLevel:   VoiceAnchorLevel.Chapter, toNodeId:   chId);

        var result = AnchorSpanResolver.Build(rule, tables);

        Assert.False(result.IsDangling);
        Assert.Equal(new StoryPosition("V1", "P1", "C1", Min, Min), result.From);
        Assert.Equal(new StoryPosition("V1", "P1", "C1", Max, Max), result.To);
    }

    // ── Paragraph anchor ──────────────────────────────────────────────────────

    [Fact]
    public void ParagraphAnchor_From_ResolvesSentinels()
    {
        var paraId  = Guid.NewGuid();
        var voiceId = Guid.NewGuid();
        var tables  = new NodeOrderTables(
            new(), new(), new(),
            new Dictionary<Guid, (string, string, string, string)> { [paraId] = ("V1", "P1", "C1", "G1") },
            new());

        var rule = Rule(voiceId, fromLevel: VoiceAnchorLevel.Paragraph, fromNodeId: paraId,
                                  toLevel:   VoiceAnchorLevel.Paragraph, toNodeId:   paraId);

        var result = AnchorSpanResolver.Build(rule, tables);

        Assert.False(result.IsDangling);
        Assert.Equal(new StoryPosition("V1", "P1", "C1", "G1", Min), result.From);
        Assert.Equal(new StoryPosition("V1", "P1", "C1", "G1", Max), result.To);
    }

    // ── ParagraphItem anchor ─────────────────────────────────────────────────

    [Fact]
    public void ParagraphItemAnchor_ReturnsFullPositionForBothBounds()
    {
        var itemId  = Guid.NewGuid();
        var voiceId = Guid.NewGuid();
        var itemPos = new StoryPosition("V1", "P1", "C1", "G1", "I1");
        var tables  = new NodeOrderTables(
            new(), new(), new(), new(),
            new Dictionary<Guid, StoryPosition> { [itemId] = itemPos });

        var rule = Rule(voiceId, fromLevel: VoiceAnchorLevel.ParagraphItem, fromNodeId: itemId,
                                  toLevel:   VoiceAnchorLevel.ParagraphItem, toNodeId:   itemId);

        var result = AnchorSpanResolver.Build(rule, tables);

        Assert.False(result.IsDangling);
        Assert.Equal(itemPos, result.From);
        Assert.Equal(itemPos, result.To);
    }

    // ── Open-ended forward ────────────────────────────────────────────────────

    [Fact]
    public void OpenEndedForward_FromSet_ToNull_NotDangling()
    {
        var chId    = Guid.NewGuid();
        var voiceId = Guid.NewGuid();
        var tables  = new NodeOrderTables(
            new(), new(),
            new Dictionary<Guid, (string, string, string)> { [chId] = ("V1", "P1", "C2") },
            new(), new());

        var rule = Rule(voiceId, fromLevel: VoiceAnchorLevel.Chapter, fromNodeId: chId);

        var result = AnchorSpanResolver.Build(rule, tables);

        Assert.False(result.IsDangling);
        Assert.Equal(new StoryPosition("V1", "P1", "C2", Min, Min), result.From);
        Assert.Null(result.To);
    }

    // ── Dangling From ─────────────────────────────────────────────────────────

    [Fact]
    public void DanglingFrom_NodeMissing_IsDanglingTrue_FromNull()
    {
        var missingId = Guid.NewGuid();
        var voiceId   = Guid.NewGuid();

        var rule = Rule(voiceId, fromLevel: VoiceAnchorLevel.Chapter, fromNodeId: missingId,
                                  toLevel:   VoiceAnchorLevel.Chapter, toNodeId:   missingId);

        var result = AnchorSpanResolver.Build(rule, EmptyTables());

        Assert.True(result.IsDangling);
        Assert.Null(result.From);
    }

    // ── Dangling To ───────────────────────────────────────────────────────────

    [Fact]
    public void DanglingTo_NodeMissing_IsDanglingTrue_ToNull()
    {
        var chId      = Guid.NewGuid();
        var missingId = Guid.NewGuid();
        var voiceId   = Guid.NewGuid();
        var tables    = new NodeOrderTables(
            new(), new(),
            new Dictionary<Guid, (string, string, string)> { [chId] = ("V1", "P1", "C1") },
            new(), new());

        var rule = Rule(voiceId, fromLevel: VoiceAnchorLevel.Chapter, fromNodeId: chId,
                                  toLevel:   VoiceAnchorLevel.Chapter, toNodeId:   missingId);

        var result = AnchorSpanResolver.Build(rule, tables);

        Assert.True(result.IsDangling);
        Assert.Null(result.To);
    }

    // ── Dangling on one bound still flags rule dangling ───────────────────────

    [Fact]
    public void DanglingFromOnly_StillFlagsRuleDangling()
    {
        var chId      = Guid.NewGuid();
        var missingId = Guid.NewGuid();
        var voiceId   = Guid.NewGuid();
        var tables    = new NodeOrderTables(
            new(), new(),
            new Dictionary<Guid, (string, string, string)> { [chId] = ("V1", "P1", "C1") },
            new(), new());

        // From = missing chapter, To = valid chapter
        var rule = Rule(voiceId, fromLevel: VoiceAnchorLevel.Chapter, fromNodeId: missingId,
                                  toLevel:   VoiceAnchorLevel.Chapter, toNodeId:   chId);

        var result = AnchorSpanResolver.Build(rule, tables);

        Assert.True(result.IsDangling);
    }
}
