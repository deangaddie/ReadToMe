using Read2Me.Services.Mutations;

namespace Read2Me.Services.History;

/// <summary>
/// What a step's commits changed, merged from their receipts' facets and ids — what the step's
/// "what changed" is written from.
/// </summary>
internal sealed record StepChanges(
    BookFacets Facets,
    IReadOnlySet<Guid> NodeIds,
    IReadOnlySet<Guid> ParagraphIds,
    IReadOnlySet<Guid> ParagraphItemIds)
{
    public static StepChanges None { get; } = new(BookFacets.None, new HashSet<Guid>(), new HashSet<Guid>(), new HashSet<Guid>());

    public StepChanges With(BookMutationEffects effects) => new(
        Facets | effects.Facets,
        new HashSet<Guid>(NodeIds.Concat(effects.NodeIds)),
        new HashSet<Guid>(ParagraphIds.Concat(effects.ParagraphIds)),
        new HashSet<Guid>(ParagraphItemIds.Concat(effects.ParagraphItemIds)));
}
